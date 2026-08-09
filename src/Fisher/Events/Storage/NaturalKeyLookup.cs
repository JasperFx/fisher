using Fisher.Storage;
using JasperFx;
using JasperFx.Events;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;
using Weasel.Sqlite;

namespace Fisher.Events.Storage;

/// <summary>
///     Resolves a natural key to the stream that holds it (fisher#40).
/// </summary>
/// <remarks>
///     <para>
///         <b>One statement, joining <c>fi_streams</c> — but not for the reason Polecat's does.</b>
///         Polecat's join exists to take a row lock on the stream (<c>UPDLOCK, HOLDLOCK</c>) in the
///         same round trip that resolves the key. Fisher has no row lock to take: exclusive fetching
///         here is the optimistic kind, which is the documented divergence. The join earns its place
///         anyway, because <c>fi_streams</c> is where <c>is_archived</c> lives and reading it there
///         rather than copying the flag into the lookup table is what removes a whole sync path.
///     </para>
///     <para>
///         <b>Resolving outside the write transaction is safe, and this is the same argument the
///         optimistic append rests on.</b> The version guard runs inside the write transaction
///         regardless, so a resolution that has gone stale by the time the caller commits fails the
///         commit rather than writing a wrong version. What a lock would buy is the loser waiting
///         instead of failing, which is exactly the trade Fisher already makes everywhere else.
///     </para>
///     <para>
///         The lookup is a primary-key seek against an embedded database, so the two-statement shape a
///         caller ends up with — resolve, then fetch the stream — costs nothing worth optimising away.
///         There is no round trip to amortise.
///     </para>
/// </remarks>
internal sealed class NaturalKeyLookup
{
    private readonly EventGraph _graph;

    internal NaturalKeyLookup(EventGraph graph)
    {
        _graph = graph;
    }

    /// <summary>
    ///     The stream identity a key names, or null when no live stream holds it.
    /// </summary>
    internal async Task<object?> ResolveAsync(NaturalKeyDefinition definition, object key, string tenantId,
        SqliteConnection connection, CancellationToken token)
    {
        var conjoined = _graph.TenancyStyle == TenancyStyle.Conjoined;
        var guids = _graph.StreamIdentity == StreamIdentity.AsGuid;
        var streamColumn = guids ? "stream_id" : "stream_key";
        var table = _graph.QuotedNaturalKeyTableName(definition.AggregateType);

        var builder = new CommandBuilder();
        builder.Append($"select k.{streamColumn} from {table} k inner join {_graph.StreamsTableName} s "
                       + $"on s.id = k.{streamColumn} where k.{Schema.NaturalKeyTable.KeyColumn} = ");
        builder.AppendParameter(key);

        // Archived streams are filtered here rather than by a flag on the lookup table — see
        // NaturalKeyTable for why fi_streams is the single source of truth for this.
        builder.Append(" and s.is_archived = 0");

        if (conjoined)
        {
            builder.Append($" and k.{StorageConstants.TenantIdColumn} = ");
            builder.AppendParameter(tenantId);
            builder.Append($" and s.{StorageConstants.TenantIdColumn} = ");
            builder.AppendParameter(tenantId);
        }

        await using var command = (SqliteCommand)builder.Compile();
        command.Connection = connection;

        var raw = await command.ExecuteScalarAsync(token).ConfigureAwait(false);

        if (raw is null or DBNull)
        {
            return null;
        }

        // Explicit, as every other Fisher read of a stream identity is: fi_streams.id is TEXT, and a
        // Guid comes back as the lowercase canonical string it was written as.
        return guids ? Guid.Parse((string)raw) : (string)raw;
    }
}
