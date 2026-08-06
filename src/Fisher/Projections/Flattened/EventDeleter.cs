using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Fisher.Events;
using JasperFx.Events;
using Weasel.Core;
using Weasel.Sqlite;

namespace Fisher.Projections.Flattened;

/// <summary>
///     Removes a flat table's row when its event type arrives.
/// </summary>
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification =
        "Class-level: builds a typed accessor delegate over TEvent's primary-key members with Expression.Compile. TEvent flows in from FlatTableProjection.Delete<TEvent>() on the caller side and is preserved per the AOT publishing guide.")]
internal sealed class EventDeleter<TEvent> : IFlatTableEventHandler
{
    private readonly FlatTableProjection _parent;
    private readonly MemberInfo[]? _primaryKeyMembers;

    private string? _sql;
    private IParameterSetter? _primaryKeySetter;

    internal EventDeleter(FlatTableProjection parent, MemberInfo[]? primaryKeyMembers)
    {
        _parent = parent;
        _primaryKeyMembers = primaryKeyMembers;
    }

    public void Compile(EventGraph events)
    {
        _primaryKeySetter = _primaryKeyMembers is not null
            ? FlatTableExpressions.SetterForMembers<TEvent>(_primaryKeyMembers)
            : events.StreamIdentity == StreamIdentity.AsGuid
                ? new StreamIdParameterSetter()
                : new StreamKeyParameterSetter();

        var table = _parent.Table;
        var primaryKey = table.PrimaryKeyColumns.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"The flat table '{table.Identifier.Name}' has no primary key column. Declare one in the "
                + "projection's constructor with Table.AddColumn(...).AsPrimaryKey() before mapping events.");

        _sql = $"delete from {SchemaUtils.QuoteName(table.Identifier.Name)} "
               + $"where {SchemaUtils.QuoteName(primaryKey)} = @p0;";
    }

    public FlatTableSqlOperation CreateOperation(IEvent e)
        => new(
            _sql ?? throw new InvalidOperationException("This flat table deleter has not been compiled."),
            e,
            [_primaryKeySetter!],
            OperationRole.Deletion);
}
