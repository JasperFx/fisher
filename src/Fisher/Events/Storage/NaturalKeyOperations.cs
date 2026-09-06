using System.Data.Common;
using Fisher.Storage;
using JasperFx;
using JasperFx.Events;
using JasperFx.MultiTenancy;
using Weasel.Core;
using Weasel.Storage;

namespace Fisher.Events.Storage;

/// <summary>
///     The natural key lookup's write SQL, in one place, for the append path and the replay path alike
///     (fisher#206).
/// </summary>
/// <remarks>
///     <para>
///         <b>Two statements, one builder, and that is the point of the file.</b> The append path
///         refuses a key already mapped to a different live stream; the replay path is last-writer-wins
///         and refuses nothing. Everything else about them — the table, the columns, the tenant term,
///         the lowercase-canonical Guid conversion — has to stay identical, and the way two upserts
///         over one table drift is by being written twice.
///     </para>
/// </remarks>
internal static class NaturalKeySql
{
    /// <summary>
    ///     Which stream column this store's identity style uses.
    /// </summary>
    internal static string StreamColumn(EventGraph graph)
        => graph.StreamIdentity == StreamIdentity.AsGuid ? "stream_id" : "stream_key";

    /// <summary>
    ///     Render a stream's identity the way every lookup reads it back.
    /// </summary>
    /// <remarks>
    ///     Lowercase canonical text, the recurring trap — the third table where binding a raw
    ///     <see cref="Guid" /> would write an uppercase value that no lookup ever matches, after
    ///     documents and tag rows, with the identical failure mode: every resolution comes back empty.
    /// </remarks>
    internal static object StreamValue(EventGraph graph, Guid streamId, string? streamKey)
        => graph.StreamIdentity == StreamIdentity.AsGuid
            ? SqliteStorageDialect<Guid>.ToDatabaseValue(streamId)
            : streamKey!;

    internal static string Tenant(EventGraph graph, string? tenantId)
        => graph.TenancyStyle == TenancyStyle.Conjoined
            ? tenantId ?? StorageConstants.DefaultTenantId
            : StorageConstants.DefaultTenantId;

    /// <summary>
    ///     The insert half both statements share, up to and including the values list.
    /// </summary>
    private static void AppendInsert(ICommandBuilder builder, EventGraph graph, Type aggregateType,
        object key, object streamValue, string tenantId)
    {
        var conjoined = graph.TenancyStyle == TenancyStyle.Conjoined;
        var table = graph.QuotedNaturalKeyTableName(aggregateType);

        builder.Append($"insert into {table} (");

        if (conjoined)
        {
            builder.Append($"{StorageConstants.TenantIdColumn}, ");
        }

        builder.Append($"{Schema.NaturalKeyTable.KeyColumn}, {StreamColumn(graph)}) values (");

        if (conjoined)
        {
            builder.AppendParameter(tenantId);
            builder.Append(", ");
        }

        builder.AppendParameter(key);
        builder.Append(", ");
        builder.AppendParameter(streamValue);
        builder.Append(")");
    }

    /// <summary>
    ///     The append path's claim: a key already mapped to a different stream is refused rather than
    ///     repointed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The refusal is the SQL, not a pre-flight read</b> — the same property marten#5349
    ///         states for the Postgres form, and the reason it is not negotiable: a probing SELECT
    ///         before the write races, so two sessions could both find the key free and the loser's
    ///         upsert would repoint the row exactly as an unguarded one does. Here the guard is a
    ///         <c>where</c> on the <c>do update</c>, so a conflicting claimant matches no row, updates
    ///         nothing, and the <c>returning</c> clause yields nothing — and the row-level lock the
    ///         upsert already takes is what serialises concurrent claimants.
    ///     </para>
    ///     <para>
    ///         Reading "no row" as the failure is the same shape the optimistic document upsert already
    ///         uses for its version guard, and it is why this operation is <em>not</em> a
    ///         <see cref="NoDataReturnedCall" />.
    ///     </para>
    /// </remarks>
    internal static void AppendClaim(ICommandBuilder builder, EventGraph graph, Type aggregateType,
        object key, object streamValue, string tenantId)
    {
        var table = graph.QuotedNaturalKeyTableName(aggregateType);
        var column = StreamColumn(graph);

        AppendInsert(builder, graph, aggregateType, key, streamValue, tenantId);

        builder.Append($" on conflict do update set {column} = excluded.{column} "
                       + $"where {table}.{column} = excluded.{column} "
                       + $"returning {column}");
    }

    /// <summary>
    ///     The replay path's write, which stays last-writer-wins.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A replay must not re-adjudicate a claim the append path already accepted</b> — the
    ///         marten#4966 ruling, and it is the difference between the two statements rather than an
    ///         oversight. Turning a pre-existing data condition into a daemon failure would strand the
    ///         shard with no caller present to correct the key derivation the refusal blames; and there
    ///         is nothing legitimate to refuse, because a refused append rolls its own events back, so
    ///         a replay never meets the losing stream's events at all.
    ///     </para>
    ///     <para>
    ///         Replay order preserves a rename for the same reason the append path's does: this
    ///         stream's superseded rows are retired after its claim, so a run ends on the same mapping
    ///         the append path built.
    ///     </para>
    /// </remarks>
    internal static void AppendReplayUpsert(ICommandBuilder builder, EventGraph graph, Type aggregateType,
        object key, object streamValue, string tenantId)
    {
        var column = StreamColumn(graph);

        AppendInsert(builder, graph, aggregateType, key, streamValue, tenantId);

        builder.Append($" on conflict do update set {column} = excluded.{column}");
    }

    /// <summary>
    ///     Drop any other key this stream was previously mapped to.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         polecat#435 / marten#5041. A stream has exactly one <em>current</em> natural key, so a
    ///         rename retires the one it supersedes — and a retired alias that resolves forever also
    ///         occupies its slot in the lookup's primary key forever, so no other stream could ever
    ///         claim that identifier again.
    ///     </para>
    ///     <para>
    ///         Scoped to the stream, so a key belonging to some other stream is untouched; and scoped
    ///         to the tenant under conjoined tenancy, where a stream id is only unique within one.
    ///     </para>
    /// </remarks>
    internal static void AppendRetire(ICommandBuilder builder, EventGraph graph, Type aggregateType,
        object key, object streamValue, string tenantId)
    {
        var table = graph.QuotedNaturalKeyTableName(aggregateType);

        builder.Append($"delete from {table} where {StreamColumn(graph)} = ");
        builder.AppendParameter(streamValue);
        builder.Append($" and {Schema.NaturalKeyTable.KeyColumn} <> ");
        builder.AppendParameter(key);

        if (graph.TenancyStyle == TenancyStyle.Conjoined)
        {
            builder.Append($" and {StorageConstants.TenantIdColumn} = ");
            builder.AppendParameter(tenantId);
        }
    }
}

/// <summary>
///     One stream's claim on one natural key, queued onto the unit of work that is appending its
///     events (fisher#206).
/// </summary>
/// <remarks>
///     <para>
///         <b>Queued rather than executed, which is what moved the whole feature onto the projection
///         path.</b> A key registered outside the append's transaction is the failure mode with teeth —
///         a crash between the two leaves either a stream no key resolves to or a key naming a stream
///         that does not exist — and an operation on the session's queue commits with the events by
///         construction, where the old writer had to be positioned by hand.
///     </para>
///     <para>
///         <b>The retirement is a second statement in the same command, after the claim.</b> Marten
///         queues its retirement <em>ahead</em> of the claim; here the order is kept as it was, because
///         a rename refused for a key that is live on another stream must leave this stream's existing
///         mapping alone. Both orders are in fact safe — the refusal aborts the batch, so nothing the
///         command did survives — but the one that reads correctly on its own is the one to keep.
///     </para>
/// </remarks>
internal sealed class NaturalKeyClaimOperation : Weasel.Storage.IStorageOperation
{
    private readonly EventGraph _graph;
    private readonly Type _aggregateType;
    private readonly object _key;
    private readonly object _streamValue;
    private readonly string _tenantId;

    internal NaturalKeyClaimOperation(EventGraph graph, Type aggregateType, object key, object streamValue,
        string tenantId)
    {
        _graph = graph;
        _aggregateType = aggregateType;
        _key = key;
        _streamValue = streamValue;
        _tenantId = tenantId;
    }

    public Type DocumentType => _aggregateType;

    public OperationRole Role() => OperationRole.Other;

    public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
    {
        NaturalKeySql.AppendClaim(builder, _graph, _aggregateType, _key, _streamValue, _tenantId);
        builder.Append("; ");
        NaturalKeySql.AppendRetire(builder, _graph, _aggregateType, _key, _streamValue, _tenantId);
    }

    public async Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        // No row means the guard matched nothing, which means the key is already mapped to a different
        // stream. Same reading the optimistic document upsert gives its own empty result.
        if (!await reader.ReadAsync(token).ConfigureAwait(false))
        {
            exceptions.Add(new Exceptions.DuplicateNaturalKeyException(_aggregateType, _key));
        }
    }
}

/// <summary>
///     The replay path's write for one event carrying one natural key (fisher#206).
/// </summary>
/// <remarks>
///     Last-writer-wins, and marked <see cref="NoDataReturnedCall" /> because it has no
///     <c>returning</c> clause to postprocess — there is nothing to adjudicate. See
///     <see cref="NaturalKeySql.AppendReplayUpsert" /> for why.
/// </remarks>
internal sealed class NaturalKeyReplayOperation : Weasel.Storage.IStorageOperation, NoDataReturnedCall
{
    private readonly EventGraph _graph;
    private readonly Type _aggregateType;
    private readonly object _key;
    private readonly object _streamValue;
    private readonly string _tenantId;

    internal NaturalKeyReplayOperation(EventGraph graph, Type aggregateType, object key, object streamValue,
        string tenantId)
    {
        _graph = graph;
        _aggregateType = aggregateType;
        _key = key;
        _streamValue = streamValue;
        _tenantId = tenantId;
    }

    public Type DocumentType => _aggregateType;

    public OperationRole Role() => OperationRole.Other;

    public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
    {
        NaturalKeySql.AppendReplayUpsert(builder, _graph, _aggregateType, _key, _streamValue, _tenantId);
        builder.Append("; ");
        NaturalKeySql.AppendRetire(builder, _graph, _aggregateType, _key, _streamValue, _tenantId);
    }

    public Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
        => Task.CompletedTask;
}
