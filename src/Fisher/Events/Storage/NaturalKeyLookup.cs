using System.Globalization;
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
///         same round trip that resolves the key. Fisher has no row lock to take: an exclusive fetch takes
///         the file's write lock before it resolves (fisher#689), which covers the key too. The join earns its place
///         anyway, because <c>fi_streams</c> is where <c>is_archived</c> lives and reading it there
///         rather than copying the flag into the lookup table is what removes a whole sync path.
///     </para>
///     <para>
///         <b>Resolving outside the write transaction is safe, and this is the same argument the
///         optimistic append rests on.</b> The version guard runs inside the write transaction
///         regardless, so a resolution that has gone stale by the time the caller commits fails the
///         commit rather than writing a wrong version. A caller who would rather wait than fail uses
///         <c>FetchForExclusiveWriting</c>, which resolves inside the lock.
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
    /// <remarks>
    ///     A mapped key (fisher#679) reads its source's table: <see cref="NaturalKeyDefinition.LookupAggregateType" />
    ///     is the owner's aggregate type for a mapped key and the key's own for an owned one, so the owner keeping
    ///     its lookup current inline is what makes an async read model reachable by key.
    /// </remarks>
    internal async Task<object?> ResolveAsync(NaturalKeyDefinition definition, object key, string tenantId,
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken token)
    {
        var resolved = await ResolveManyAsync(definition, [key], tenantId, connection, transaction, token)
            .ConfigureAwait(false);

        return resolved.Count == 0 ? null : resolved.Values.First();
    }

    /// <summary>
    ///     The stream identity each key names, keyed by <see cref="KeyText" />. A key naming no live stream is
    ///     left out. One statement however many keys there are.
    /// </summary>
    internal async Task<Dictionary<string, object>> ResolveManyAsync(NaturalKeyDefinition definition,
        IReadOnlyList<object> keys, string tenantId, SqliteConnection connection, SqliteTransaction? transaction,
        CancellationToken token)
    {
        var conjoined = _graph.TenancyStyle == TenancyStyle.Conjoined;
        var guids = _graph.StreamIdentity == StreamIdentity.AsGuid;
        var streamColumn = guids ? "stream_id" : "stream_key";
        var table = _graph.QuotedNaturalKeyTableName(definition.LookupAggregateType);
        var keyColumn = Schema.NaturalKeyTable.KeyColumn;

        var builder = new CommandBuilder();
        builder.Append($"select k.{keyColumn}, k.{streamColumn} from {table} k inner join {_graph.StreamsTableName} s "
                       + $"on s.id = k.{streamColumn} where ");

        if (keys.Count == 1)
        {
            builder.Append($"k.{keyColumn} = ");
            builder.AppendParameter(keys[0]);
        }
        else
        {
            // One parameter for the whole list, so a long list cannot run into SQLite's variable limit.
            builder.Append($"k.{keyColumn} in (select value from json_each(");
            builder.AppendParameter(SqliteJsonArrays.Write(keys));
            builder.Append("))");
        }

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

        // A session holding an exclusive lock (fisher#689), or enlisted in a caller's transaction, has a
        // transaction pending on this connection, and Microsoft.Data.Sqlite refuses a detached command
        // that does not name it.
        command.Transaction = transaction;

        var resolved = new Dictionary<string, object>(keys.Count, StringComparer.Ordinal);

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            // Explicit, as every other Fisher read of a stream identity is: fi_streams.id is TEXT, and a
            // Guid comes back as the lowercase canonical string it was written as.
            var stream = reader.GetString(1);
            resolved[KeyText(reader.GetValue(0))] = guids ? Guid.Parse(stream) : stream;
        }

        return resolved;
    }

    /// <summary>
    ///     A key's value as text, so a key handed in as an <c>int</c> matches the <c>long</c> SQLite hands back.
    /// </summary>
    internal static string KeyText(object key) => Convert.ToString(key, CultureInfo.InvariantCulture)!;
}
