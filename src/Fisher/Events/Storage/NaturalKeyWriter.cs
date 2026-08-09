using Fisher.Storage;
using JasperFx;
using JasperFx.Events;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;
using Weasel.Sqlite;

namespace Fisher.Events.Storage;

/// <summary>
///     Writes the natural key rows for a unit of work's appended events (fisher#40).
/// </summary>
/// <remarks>
///     <para>
///         <b>Inside the append's transaction, which is the whole point.</b> A key registered outside
///         it is the failure mode with teeth: a crash between the two leaves either a stream no key
///         resolves to, or a key pointing at a stream that does not exist. Same reasoning as
///         <see cref="EventTagWriter" />, and it runs beside it for the same reason — though unlike a
///         tag row a key row needs nothing from the append's postprocessing, since the stream id is
///         known before a single event is written.
///     </para>
///     <para>
///         <b>Fisher writes these from the session rather than from an inline projection, where
///         Polecat registers a <c>NaturalKeyProjection</c>.</b> A natural key row is an index over
///         streams, not a projection of them — and being a projection is what forces Polecat to carry a
///         second, rebuild-time entry point, because a daemon rebuild replays events without appending
///         streams and its lookup table would otherwise be left empty by the teardown. Nothing here is
///         reachable from a rebuild, so there is nothing to repopulate.
///     </para>
///     <para>
///         <b>The conflict clause is guarded, and the guard is what makes a duplicate key an error.</b>
///         Every event carrying the key rewrites the row, so re-asserting the same mapping has to be
///         idempotent; a <em>different</em> stream claiming the key must not silently take it, which is
///         what Polecat's unguarded <c>MERGE</c> does. So the update carries
///         <c>where stream_id = excluded.stream_id</c> and the statement returns the row it settled on:
///         same stream returns it, a new key returns it, and a conflicting stream matches nothing and
///         returns nothing. Reading "no row" as the failure is the same shape the optimistic document
///         upsert already uses for its version guard.
///     </para>
/// </remarks>
internal sealed class NaturalKeyWriter
{
    private readonly EventGraph _graph;
    private readonly IReadOnlyList<NaturalKeyDefinition> _definitions;

    internal NaturalKeyWriter(EventGraph graph, IReadOnlyList<NaturalKeyDefinition> definitions)
    {
        _graph = graph;
        _definitions = definitions;
    }

    internal async Task WriteAsync(IReadOnlyList<StreamAction> streams, SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (_definitions.Count == 0)
        {
            return;
        }

        foreach (var stream in streams)
        {
            foreach (var definition in _definitions)
            {
                foreach (var @event in stream.Events)
                {
                    var mapping = definition.EventMappings
                        .FirstOrDefault(x => x.EventType.IsAssignableFrom(@event.Data.GetType()));

                    // The extractor takes the whole IEvent rather than its body, so a key can be
                    // derived from metadata as well as from the event's own members — jasperfx#569.
                    if (mapping?.Extractor(@event) is not { } value)
                    {
                        continue;
                    }

                    if (definition.Unwrap(value) is { } unwrapped)
                    {
                        await WriteOneAsync(definition, unwrapped, stream, connection, transaction, token)
                            .ConfigureAwait(false);
                    }
                }
            }
        }
    }

    private async Task WriteOneAsync(NaturalKeyDefinition definition, object key, StreamAction stream,
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        var conjoined = _graph.TenancyStyle == TenancyStyle.Conjoined;
        var guids = _graph.StreamIdentity == StreamIdentity.AsGuid;
        var streamColumn = guids ? "stream_id" : "stream_key";
        var table = _graph.QuotedNaturalKeyTableName(definition.AggregateType);

        var builder = new CommandBuilder();
        builder.Append($"insert into {table} (");

        if (conjoined)
        {
            builder.Append($"{StorageConstants.TenantIdColumn}, ");
        }

        builder.Append($"{Schema.NaturalKeyTable.KeyColumn}, {streamColumn}) values (");

        if (conjoined)
        {
            builder.AppendParameter(stream.TenantId ?? StorageConstants.DefaultTenantId);
            builder.Append(", ");
        }

        builder.AppendParameter(key);
        builder.Append(", ");

        // Lowercase canonical text, the recurring trap — this is the third table where binding a raw
        // Guid would write an uppercase value that no lookup ever matches, after documents and tag
        // rows, and the failure mode is identical: every resolution comes back empty.
        builder.AppendParameter(guids
            ? SqliteStorageDialect<Guid>.ToDatabaseValue(stream.Id)
            : stream.Key!);

        builder.Append($") on conflict do update set {streamColumn} = excluded.{streamColumn} "
                       + $"where {table}.{streamColumn} = excluded.{streamColumn} "
                       + $"returning {streamColumn}");

        await using var command = (SqliteCommand)builder.Compile();
        command.Connection = connection;
        command.Transaction = transaction;

        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is null)
        {
            throw new Exceptions.DuplicateNaturalKeyException(definition.AggregateType, key);
        }
    }
}
