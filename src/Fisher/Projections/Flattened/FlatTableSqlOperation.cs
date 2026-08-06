using System.Data.Common;
using JasperFx.Events;
using Weasel.Core;
using Weasel.Storage;

namespace Fisher.Projections.Flattened;

/// <summary>
///     One flat-table upsert or delete, queued onto the session so it commits with everything else in
///     the unit of work.
/// </summary>
/// <remarks>
///     <para>
///         Queued rather than executed, exactly as a snapshot write is: inline, that puts the row in
///         the same transaction as the event that produced it; under the daemon, in the same
///         transaction as the shard's progression row.
///     </para>
///     <para>
///         <see cref="DocumentType" /> is <see cref="object" /> because a flat table is not a document
///         type — which is also how the session's commit-time table check knows to leave it alone. The
///         table is created by the migration instead; see <see cref="FlatTableFeatureSchema" />.
///     </para>
/// </remarks>
internal sealed class FlatTableSqlOperation : Weasel.Storage.IStorageOperation
{
    private readonly string _sql;
    private readonly IEvent _source;
    private readonly IParameterSetter[] _setters;
    private readonly OperationRole _role;

    internal FlatTableSqlOperation(string sql, IEvent source, IParameterSetter[] setters, OperationRole role)
    {
        _sql = sql;
        _source = source;
        _setters = setters;
        _role = role;
    }

    public Type DocumentType => typeof(object);

    public OperationRole Role() => _role;

    public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
    {
        builder.Append(_sql);

        var parameters = new Dictionary<string, object?>();

        for (var i = 0; i < _setters.Length; i++)
        {
            parameters[$"p{i}"] = _setters[i].ValueFor(_source);
        }

        builder.AddParameters(parameters);
    }

    public Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
        => Task.CompletedTask;
}
