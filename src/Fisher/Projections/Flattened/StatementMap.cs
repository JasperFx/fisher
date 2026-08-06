using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Fisher.Events;
using JasperFx.Core;
using JasperFx.Events;
using Weasel.Core;
using Weasel.Sqlite;
using Weasel.Sqlite.Tables;

namespace Fisher.Projections.Flattened;

/// <summary>
///     The column mappings for one event type, compiled into a single SQLite upsert.
/// </summary>
/// <remarks>
///     <para>
///         <strong>One <c>insert … on conflict … do update</c>, where Polecat emits a <c>MERGE</c> and
///         Marten generates a per-event upsert function.</strong> SQLite has had upsert syntax since
///         3.24, so the matched and not-matched branches are two clauses of one statement rather than
///         two statements — and because it is one statement, a parameter appearing in both branches is
///         bound once by name instead of being duplicated.
///     </para>
///     <para>
///         Columns are added to the table definition as they are mapped, so the table's shape is
///         whatever the mappings collectively named. A column mapped twice — by two event types, or
///         once by name and once derived from a member — resolves to the single existing column rather
///         than producing duplicate DDL.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification =
        "Class-level: builds typed accessor delegates over TEvent's members with Expression.Compile. TEvent flows in from FlatTableProjection.Project<TEvent>() on the caller side and is preserved per the AOT publishing guide.")]
public class StatementMap<TEvent> : IFlatTableEventHandler
{
    private readonly FlatTableProjection _parent;
    private readonly List<IColumnMap> _columnMaps = [];
    private readonly List<IParameterSetter> _memberSetters = [];

    private IParameterSetter? _primaryKeySetter;
    private string? _sql;
    private IParameterSetter[]? _setters;

    internal StatementMap(FlatTableProjection parent, MemberInfo[]? primaryKeyMembers)
    {
        _parent = parent;

        if (primaryKeyMembers is not null)
        {
            _primaryKeySetter = FlatTableExpressions.SetterForMembers<TEvent>(primaryKeyMembers);
        }
    }

    /// <summary>
    ///     Write an event member straight into a column.
    /// </summary>
    /// <param name="member">The member to read.</param>
    /// <param name="columnName">
    ///     The column to write. Defaults to the member's name in snake case, so <c>MemberCount</c>
    ///     becomes <c>member_count</c>.
    /// </param>
    public Table.ColumnExpression Map<TValue>(Expression<Func<TEvent, TValue>> member, string? columnName = null)
        => AddMemberMap(member, columnName, name => new MemberMap(name));

    /// <summary>Add an event member's value to a column.</summary>
    public Table.ColumnExpression Increment<TValue>(Expression<Func<TEvent, TValue>> member,
        string? columnName = null)
        => AddMemberMap(member, columnName, name => new IncrementMemberMap(name));

    /// <summary>Subtract an event member's value from a column.</summary>
    public Table.ColumnExpression Decrement<TValue>(Expression<Func<TEvent, TValue>> member,
        string? columnName = null)
        => AddMemberMap(member, columnName, name => new DecrementMemberMap(name));

    /// <summary>Add one to a column.</summary>
    public Table.ColumnExpression Increment(string columnName)
    {
        _columnMaps.Add(new IncrementMap(columnName));
        return ResolveColumn(columnName, typeof(int));
    }

    /// <summary>Subtract one from a column.</summary>
    public Table.ColumnExpression Decrement(string columnName)
    {
        _columnMaps.Add(new DecrementMap(columnName));
        return ResolveColumn(columnName, typeof(int));
    }

    /// <summary>Set a column to a fixed string.</summary>
    public Table.ColumnExpression SetValue(string columnName, string value)
    {
        _columnMaps.Add(new SetStringValueMap(columnName, value));
        return ResolveColumn(columnName, typeof(string));
    }

    /// <summary>Set a column to a fixed integer.</summary>
    public Table.ColumnExpression SetValue(string columnName, int value)
    {
        _columnMaps.Add(new SetIntValueMap(columnName, value));
        return ResolveColumn(columnName, typeof(int));
    }

    private Table.ColumnExpression AddMemberMap<TValue>(Expression<Func<TEvent, TValue>> member,
        string? columnName, Func<string, IColumnMap> build)
    {
        var name = columnName ?? FlatTableExpressions.SnakeCase(FlatTableExpressions.MemberOf(member).Name);

        _columnMaps.Add(build(name));
        _memberSetters.Add(new EventDataParameterSetter<TEvent, TValue>(member.Compile()));

        return ResolveColumn(name, typeof(TValue));
    }

    void IFlatTableEventHandler.Compile(EventGraph events)
    {
        // With no explicit primary key source the row is keyed on the stream, which is the ordinary
        // case and the only one the compliance suite exercises.
        _primaryKeySetter ??= events.StreamIdentity == StreamIdentity.AsGuid
            ? new StreamIdParameterSetter()
            : new StreamKeyParameterSetter();

        var table = _parent.Table;
        var primaryKey = table.PrimaryKeyColumns.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"The flat table '{table.Identifier.Name}' has no primary key column. Declare one in the "
                + $"projection's constructor with Table.AddColumn(...).AsPrimaryKey() before mapping events.");

        var quotedKey = SchemaUtils.QuoteName(primaryKey);

        // Parameter 0 is always the key; the maps that read from the event take 1..N in order.
        var setters = new List<IParameterSetter> { _primaryKeySetter };

        var insertColumns = new List<string> { quotedKey };
        var insertValues = new List<string> { "@p0" };
        var updateAssignments = new List<string>();

        var memberIndex = 0;

        foreach (var map in _columnMaps)
        {
            var parameterName = string.Empty;

            if (map.RequiresInput)
            {
                parameterName = $"@p{setters.Count}";
                setters.Add(_memberSetters[memberIndex++]);
            }

            insertColumns.Add(SchemaUtils.QuoteName(map.ColumnName));
            insertValues.Add(map.InsertExpression(parameterName));
            updateAssignments.Add(map.UpdateExpression(parameterName));
        }

        var quotedTable = SchemaUtils.QuoteName(table.Identifier.Name);

        // No "do nothing" branch: a Project<T> with only SetValue mappings still has assignments,
        // because the primary key alone never appears on the left of the update.
        _sql = $"""
                insert into {quotedTable} ({insertColumns.Join(", ")})
                values ({insertValues.Join(", ")})
                on conflict ({quotedKey}) do update set {updateAssignments.Join(", ")};
                """;

        _setters = setters.ToArray();
    }

    FlatTableSqlOperation IFlatTableEventHandler.CreateOperation(IEvent e)
        => new(
            _sql ?? throw new InvalidOperationException("This flat table mapping has not been compiled."),
            e,
            _setters!,
            OperationRole.Upsert);

    /// <summary>
    ///     Find or add the table column this mapping writes.
    /// </summary>
    /// <remarks>
    ///     Matched case-insensitively even though SQLite compares <em>values</em> case-sensitively:
    ///     identifiers are the exception, so two mappings naming <c>Amount</c> and <c>amount</c> mean
    ///     one column, and adding both would emit DDL SQLite rejects as a duplicate.
    /// </remarks>
    private Table.ColumnExpression ResolveColumn(string columnName, Type dotnetType)
    {
        var table = _parent.Table;

        var existing = table.Columns
            .FirstOrDefault(x => string.Equals(x.Name, columnName, StringComparison.OrdinalIgnoreCase));

        return existing is not null
            ? new Table.ColumnExpression(table, existing)
            : table.AddColumn(columnName, FlatTableExpressions.ColumnTypeFor(dotnetType));
    }
}
