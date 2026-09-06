using Fisher.Storage;
using Microsoft.Data.Sqlite;

namespace Fisher.Internal;

/// <summary>
///     Delete one tenant's rows, leaving every other tenant's alone and leaving the schema — and, under
///     database-per-tenant, the tenant's file — where they were (fisher#173).
/// </summary>
/// <remarks>
///     <para>
///         <b>This is not the tenant deletion Fisher refuses, and the distinction is the whole reason it
///         can exist.</b> Deprovisioning a tenant here means deleting a <em>file</em> — the cheapest
///         deprovisioning of any Critter Stack store and the most irreversible, and Fisher cannot know
///         whether that file is backed up — so the tenancy API suspends or forgets and an operator
///         removes the file themselves. Wiping a tenant's <em>rows</em> out of a shared conjoined file
///         is a different operation: it destroys nothing an operator would have to restore a file to
///         recover, it is the only way to erase a conjoined tenant at all, and it was covered by
///         nothing.
///     </para>
///     <para>
///         <b>Under database-per-tenant it clears the tenant's file rather than deleting it</b>, which
///         is the same operation reached from the other side. The file, its schema and its pooled
///         connections survive, so the tenant goes on working and simply has no data; removing the file
///         stays the operator's act.
///     </para>
/// </remarks>
internal sealed class TenantDataCleaner
{
    private readonly DocumentStore _store;
    private readonly string _tenantId;

    internal TenantDataCleaner(string tenantId, DocumentStore store)
    {
        _tenantId = tenantId;
        _store = store;
    }

    private string Prefix => FisherTableNaming.PrefixFor(_store.Options.DatabaseSchemaName);

    internal async Task ExecuteAsync(CancellationToken token)
    {
        // Resolves through the tenancy, so an unknown or suspended tenant is refused by name here
        // exactly as it would be by a session — a wipe that silently landed on the default tenant's
        // file is the one outcome database-per-tenant exists to make impossible.
        var database = _store.Tenancy.DatabaseFor(_tenantId);

        var events = _store.Options.EventGraph;

        // Tag rows carry no tenant of their own and have a real foreign key to fi_events(seq_id), so
        // they are reached through their events and have to go first — the ordering fisher#6
        // established for DeleteAllEventDataAsync, now with a tenant predicate on the subselect.
        var tagTables = events.TagTypes.Select(events.TagTableName).ToArray();

        await _store.Options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await database.OpenConnectionAsync(ct).ConfigureAwait(false);

            var tables = (await ReadTableNamesAsync(connection, ct).ConfigureAwait(false))
                .Where(x => x.StartsWith(Prefix, StringComparison.Ordinal))
                .ToList();

            var tenanted = new List<string>();

            foreach (var table in tables)
            {
                if (await HasTenantColumnAsync(connection, table, ct).ConfigureAwait(false))
                {
                    tenanted.Add(table);
                }
            }

            // **"Has a tenant_id column" is not the question, and reading it as one would make this
            // refusal unreachable.** fi_events, fi_streams and fi_dead_letters carry the column on
            // every store, tenanted or not — the event tables get it with a default under
            // non-conjoined tenancy (see StreamsTable), and a dead letter records the failing event's
            // tenant as ordinary data. So the question is what the store was *configured* to slice by:
            // a database per tenant, conjoined events, or at least one MultiTenanted() document type,
            // which is the only case that puts the column on a fi_doc_* table.
            var tenantedByConfiguration =
                _store.Tenancy.Cardinality != JasperFx.Descriptors.DatabaseCardinality.Single
                || events.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined
                || tenanted.Any(x => x.StartsWith(Prefix + "doc_", StringComparison.Ordinal));

            // Refused rather than silently doing nothing, which would report a successful erasure of a
            // tenant that has no rows because the store has no notion of tenants at all — the worst
            // possible answer to a compliance request.
            if (!tenantedByConfiguration)
            {
                throw new NotSupportedException(
                    $"This store keeps no tenant-scoped data, so there is nothing that could be '{_tenantId}'s' "
                    + "to delete. Conjoined tenancy (Events.TenancyStyle = TenancyStyle.Conjoined, and "
                    + "Schema.For<T>().MultiTenanted()) is what puts a tenant_id column on the tables; "
                    + "database-per-tenant (MultiTenantedDatabases(...)) gives each tenant a file of its own. "
                    + "Under neither, Advanced.Clean is the operation you want.");
            }

            var ordered = await OrderByForeignKeys(connection, tenanted, ct).ConfigureAwait(false);
            var existing = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);

            foreach (var tagTable in tagTables.Select(Unquoted).Where(existing.Contains))
            {
                await ExecuteAsync(connection,
                    $"""
                     delete from "{tagTable}"
                      where seq_id in (select seq_id from {events.EventsTableName} where tenant_id = $tenant)
                     """, ct).ConfigureAwait(false);
            }

            foreach (var table in ordered)
            {
                await ExecuteAsync(connection, $"delete from \"{table}\" where tenant_id = $tenant", ct)
                    .ConfigureAwait(false);
            }

            // Under database-per-tenant the whole file is this tenant's, so anything that carries no
            // tenant column is still this tenant's data — the natural key lookups and the progression
            // rows in particular. Leaving the lookups would make the duplicate guard fire on streams
            // that are gone, which is precisely what DeleteAllEventDataAsync clears them for.
            if (_store.Tenancy.Cardinality != JasperFx.Descriptors.DatabaseCardinality.Single)
            {
                foreach (var table in tables.Except(ordered, StringComparer.OrdinalIgnoreCase)
                             .Where(x => !x.Equals(events.ProgressionTableName, StringComparison.OrdinalIgnoreCase)))
                {
                    await ExecuteAsync(connection, $"delete from \"{table}\"", ct).ConfigureAwait(false);
                }
            }
        }, token).ConfigureAwait(false);
    }

    /// <remarks>
    ///     The names come back from <c>EventGraph</c> already quoted for embedding in SQL; matching them
    ///     against <c>sqlite_master</c> needs the bare form.
    /// </remarks>
    private static string Unquoted(string name) => name.Trim('"');

    private async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$tenant", _tenantId);

        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<bool> HasTenantColumnAsync(SqliteConnection connection, string table,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"select count(*) from pragma_table_info('{table.Replace("'", "''")}') "
            + "where name = 'tenant_id'";

        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) > 0;
    }

    private static async Task<List<string>> ReadTableNamesAsync(SqliteConnection connection,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select name from sqlite_master where type = 'table'";

        var names = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <inheritdoc cref="FisherDocumentCleaner" />
    /// <remarks>
    ///     Children first, read from <c>pragma_foreign_key_list</c> — the same pass
    ///     <c>DeleteAllDocumentsAsync</c> uses, and for the same reason: Weasel's default profile
    ///     enforces foreign keys on every connection Fisher opens, so an unordered sweep over document
    ///     tables that reference each other (fisher#38) fails roughly half the time.
    /// </remarks>
    private static async Task<List<string>> OrderByForeignKeys(SqliteConnection connection,
        List<string> tables, CancellationToken token)
    {
        var references = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"select \"table\" from pragma_foreign_key_list('{table.Replace("'", "''")}')";

            var parents = new List<string>();

            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                parents.Add(reader.GetString(0));
            }

            references[table] = parents;
        }

        var ordered = new List<string>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string table)
        {
            if (!done.Add(table) || !visiting.Add(table))
            {
                return;
            }

            foreach (var child in tables.Where(x => references[x].Contains(table, StringComparer.OrdinalIgnoreCase)))
            {
                Visit(child);
            }

            visiting.Remove(table);
            ordered.Add(table);
        }

        foreach (var table in tables)
        {
            Visit(table);
        }

        return ordered;
    }
}
