using System.Data.Common;
using Fisher.Storage;
using JasperFx;
using Weasel.Core;
using Weasel.Core.SqlGeneration;
using Weasel.Storage;

namespace Fisher.Patching;

/// <summary>
///     One <c>update … set data = &lt;nested json expression&gt;</c>, queued in the unit of work
///     (fisher#35).
/// </summary>
/// <remarks>
///     <para>
///         Every step of the patch wraps the previous one, so a whole <c>IPatchExpression</c> chain is a
///         single statement. A step that <em>reads</em> what it is about to change — an increment, an
///         append-if-not-exists, a remove — reads the accumulated expression rather than the bare
///         <c>data</c> column, so composed steps see each other's work. The cost is that the expression
///         text grows with the chain; patches are short in practice.
///     </para>
///     <para>
///         <b>The version and timestamp columns are updated here.</b> They are not part of the JSON, so
///         nothing about the json1 expression touches them — and without this an optimistic-concurrency
///         type would silently stop seeing patched writes, and <c>ModifiedSince</c> would miss them.
///     </para>
/// </remarks>
internal sealed class PatchOperation : Weasel.Storage.IStorageOperation
{
    /// <summary>
    ///     Where a bound value goes in the expression text.
    /// </summary>
    /// <remarks>
    ///     <b>A placeholder rather than the parameter's name.</b> <c>ICommandBuilder.AppendParameter</c>
    ///     writes the marker into the SQL <em>at the point it is called</em>, and the expression has to
    ///     be composed before the statement head is written — so binding while composing put
    ///     <c>@p0</c> in front of <c>update</c>. The expression is built with placeholders and split on
    ///     them at render time, which is the only ordering that works.
    /// </remarks>
    /// <remarks>
    ///     <b>Indexed, because a step may embed the accumulated expression more than once.</b>
    ///     <c>AppendIfNotExists</c> renders <c>case when … then {inner} else …{inner}… end</c>, and
    ///     <c>{inner}</c> carries the placeholders of every step before it — so a positional
    ///     placeholder would be counted twice and the values would run out. An index survives being
    ///     duplicated; the value is simply bound again, which costs one extra parameter and is correct.
    /// </remarks>
    private const char ValuePlaceholder = '\u0001';

    private readonly List<Func<string, Func<object, string>, string>> _steps = [];
    private readonly List<object> _values = [];
    private readonly DocumentMapping _mapping;
    private readonly ISqlFragment _where;
    private readonly string? _tenantId;

    internal PatchOperation(DocumentMapping mapping, ISqlFragment where, string? tenantId)
    {
        _mapping = mapping;
        _where = where;
        _tenantId = tenantId;

        DocumentType = mapping.DocumentType;
    }

    internal void Add(Func<string, Func<object, string>, string> step) => _steps.Add(step);

    public Type DocumentType { get; }

    public OperationRole Role() => OperationRole.Patch;

    public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
    {
        if (_steps.Count == 0)
        {
            // Nothing was asked for. An update with no assignment is a syntax error, and rewriting
            // every matched row's timestamp for an empty patch would be worse than doing nothing.
            builder.Append("select 1 where 0");
            return;
        }

        // Each step is handed the expression so far and a binder, and returns the expression that
        // wraps it. `data` is the innermost.
        _values.Clear();
        var expression = _steps.Aggregate("data", (inner, step) => step(inner, Record));

        builder.Append("update ");
        builder.Append(_mapping.QuotedTableName);
        builder.Append(" set data = ");
        AppendWithValues(builder, expression);

        builder.Append(", last_modified = ");
        builder.Append(SqliteTimestamp.NowExpression);

        if (_mapping.UseOptimisticConcurrency)
        {
            builder.Append(", guid_version = ");
            builder.AppendParameter(Guid.CreateVersion7().ToString());
        }

        if (_mapping.UseNumericRevisions)
        {
            builder.Append($", {NumericRevision.Column} = {NumericRevision.Column} + 1");
        }

        builder.Append(" where ");

        if (_tenantId is not null)
        {
            builder.Append(StorageConstants.TenantIdColumn);
            builder.Append(" = ");
            builder.AppendParameter(_tenantId);
            builder.Append(" and ");
        }

        // A soft-deleted row is not there as far as every other read is concerned, so a patch must not
        // reach one either — the same rule the load SQL and the LINQ default filter follow.
        if (_mapping.IsSoftDeleted)
        {
            builder.Append(SoftDelete.NotDeletedSql);
            builder.Append(" and ");
        }

        _where.Apply(builder);
    }

    private string Record(object value)
    {
        _values.Add(value);

        return $"{ValuePlaceholder}{_values.Count - 1}{ValuePlaceholder}";
    }

    /// <summary>
    ///     Write the expression, calling <c>AppendParameter</c> at each placeholder so the marker lands
    ///     exactly where the value belongs.
    /// </summary>
    private void AppendWithValues(ICommandBuilder builder, string expression)
    {
        // Odd segments are indexes, even segments are SQL — the shape Split gives for a delimiter
        // that always comes in pairs.
        var segments = expression.Split(ValuePlaceholder);

        for (var i = 0; i < segments.Length; i++)
        {
            if (i % 2 == 1)
            {
                builder.AppendParameter(_values[int.Parse(segments[i])]);
            }
            else
            {
                builder.Append(segments[i]);
            }
        }
    }

    public Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
        => Task.CompletedTask;
}
