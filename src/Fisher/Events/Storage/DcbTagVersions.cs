using System.Text;
using Fisher.Events.Schema;
using JasperFx;
using JasperFx.Events.Tags;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;
using Weasel.Core;

namespace Fisher.Events.Storage;

/// <summary>
///     One <c>fi_dcb_tag_version</c> row a DCB boundary read, and the version it saw (0 for no row).
/// </summary>
internal readonly record struct DcbTagVersionCapture(string TagTable, string TagValue, long Version);

/// <summary>
///     The SQL around <see cref="DcbTagVersionTable" /> (fisher#635): moving versions, forgetting rows no event
///     carries, and reading the rows a boundary checks.
/// </summary>
internal static class DcbTagVersions
{
    /// <summary>Marten's separator between a tag table suffix and an event type name.</summary>
    internal const char Separator = ':';

    /// <summary>The <c>tag_table</c> key a condition is checked against.</summary>
    internal static string KeyFor(ITagTypeRegistration registration, string? eventTypeName)
        => eventTypeName is null ? registration.TableSuffix : registration.TableSuffix + Separator + eventTypeName;

    /// <summary>
    ///     Move the version of every tag on the events <paramref name="appendAffected" /> selects, under both keys
    ///     (the tag value, and the tag value with the event's type), creating any row that is missing.
    /// </summary>
    /// <param name="appendAffected">Writes a select of <c>seq_id, type, tenant_id</c> from <c>fi_events</c>.</param>
    /// <remarks>
    ///     Has to run while the events and their tag rows are still there, so before a delete. Every row it
    ///     writes gets the one value it drew from the sequence, which <see cref="AppendForgetUncarried" /> uses
    ///     to find them again.
    /// </remarks>
    internal static void AppendBump(ICommandBuilder builder, EventGraph events, Action<ICommandBuilder> appendAffected)
    {
        if (events.TagTypes.Count == 0) return;

        appendNextVersion(builder, events);

        builder.Append("with affected as (");
        appendAffected(builder);
        builder.Append(")\n");

        appendInsertHead(builder, events);

        var tenant = TenantExpression(events, "a.tenant_id");
        var first = true;

        foreach (var registration in events.TagTypes)
        {
            var suffix = Literal(registration.TableSuffix);
            var keyed = Literal(registration.TableSuffix + Separator);
            var join = $" from {events.TagTableName(registration)} as t inner join affected as a on a.seq_id = t.seq_id\n";

            if (!first) builder.Append("union\n");
            first = false;

            builder.Append($"select {suffix} as tag_table, cast(t.value as text) as tag_value, {tenant} as tenant_id{join}");
            builder.Append($"union\nselect {keyed} || a.type, cast(t.value as text), {tenant}{join}");
        }

        appendInsertTail(builder, events);
    }

    /// <summary>
    ///     <see cref="AppendBump" /> for <c>AssignTagWhere</c>: the one tag value it assigns, on the events its
    ///     predicate selects.
    /// </summary>
    /// <param name="appendAffected">Writes a select of <c>type, tenant_id</c> from <c>fi_events</c>.</param>
    internal static void AppendBumpForAssignedTag(ICommandBuilder builder, EventGraph events,
        ITagTypeRegistration registration, object value, Action<ICommandBuilder> appendAffected)
    {
        appendNextVersion(builder, events);

        builder.Append("with affected as (");
        appendAffected(builder);
        builder.Append(")\n");

        appendInsertHead(builder, events);

        var tenant = TenantExpression(events, "a.tenant_id");

        builder.Append($"select {Literal(registration.TableSuffix)} as tag_table, cast(");
        builder.AppendParameter(value);
        builder.Append($" as text) as tag_value, {tenant} as tenant_id from affected as a\n");
        builder.Append($"union\nselect {Literal(registration.TableSuffix + Separator)} || a.type, cast(");
        builder.AppendParameter(value);
        builder.Append($" as text), {tenant} from affected as a\n");

        appendInsertTail(builder, events);
    }

    /// <summary>
    ///     After a tombstone has deleted its events: delete the rows the tombstone just moved that no event
    ///     carries any more, so a deleted stream's tag values do not outlive it (marten#6035).
    /// </summary>
    /// <remarks>
    ///     A boundary that captured one of these rows now finds no row, which reads as 0 and would match a
    ///     capture of 0. <see cref="ReadAsync" />'s caller covers that case by counting what the boundary read.
    /// </remarks>
    internal static void AppendForgetUncarried(ICommandBuilder builder, EventGraph events)
    {
        if (events.TagTypes.Count == 0) return;

        var table = events.DcbTagVersionTableName;
        var tenantMatch = events.TenancyStyle == TenancyStyle.Conjoined
            ? $" and e.tenant_id = {table}.tenant_id"
            : "";

        builder.Append($"delete from {table} where version = (select value from {events.DcbTagVersionSequenceTableName} where id = 1) and (");

        var first = true;
        foreach (var registration in events.TagTypes)
        {
            if (!first) builder.Append(" or ");
            first = false;

            var keyed = registration.TableSuffix + Separator;

            builder.Append($"((tag_table = {Literal(registration.TableSuffix)} or substr(tag_table, 1, {keyed.Length}) = {Literal(keyed)})");
            builder.Append($" and not exists (select 1 from {events.TagTableName(registration)} as t inner join {events.EventsTableName} as e on e.seq_id = t.seq_id");
            builder.Append($" where t.value = {table}.tag_value{tenantMatch}))");
        }

        builder.Append(");\n");
    }

    /// <summary>
    ///     The <c>fi_dcb_tag_version</c> rows for <paramref name="targets" />, a missing row as 0.
    /// </summary>
    /// <param name="transaction">The save's write transaction, or null for the capture.</param>
    internal static async Task<DcbTagVersionCapture[]> ReadAsync(EventGraph events,
        IReadOnlyList<(string TagTable, object TagValue)> targets, string tenantId, SqliteConnection connection,
        SqliteTransaction? transaction, CancellationToken token)
    {
        var sql = new StringBuilder("select tag_table, tag_value, version from ")
            .Append(events.DcbTagVersionTableName)
            .Append(" where tenant_id = @tenant and (");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@tenant", tenantId);

        for (var i = 0; i < targets.Count; i++)
        {
            if (i > 0) sql.Append(" or ");
            sql.Append($"(tag_table = @k{i} and tag_value = cast(@v{i} as text))");
            command.Parameters.AddWithValue($"@k{i}", targets[i].TagTable);
            command.Parameters.AddWithValue($"@v{i}", targets[i].TagValue);
        }

        sql.Append(')');
        command.CommandText = sql.ToString();

        var found = new Dictionary<(string, string), long>();
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                found[(reader.GetString(0), reader.GetString(1))] = reader.GetInt64(2);
            }
        }

        var captures = new DcbTagVersionCapture[targets.Count];
        for (var i = 0; i < targets.Count; i++)
        {
            var value = Convert.ToString(targets[i].TagValue, System.Globalization.CultureInfo.InvariantCulture)!;
            found.TryGetValue((targets[i].TagTable, value), out var version);
            captures[i] = new DcbTagVersionCapture(targets[i].TagTable, value, version);
        }

        return captures;
    }

    /// <summary>The tenant a row is keyed on: the event's under conjoined tenancy, otherwise the default.</summary>
    internal static string TenantFor(EventGraph events, string sessionTenant)
        => events.TenancyStyle == TenancyStyle.Conjoined ? sessionTenant : StorageConstants.DefaultTenantId;

    private static string TenantExpression(EventGraph events, string column)
        => events.TenancyStyle == TenancyStyle.Conjoined ? column : Literal(StorageConstants.DefaultTenantId);

    private static void appendNextVersion(ICommandBuilder builder, EventGraph events)
    {
        builder.Append($"""
                        insert into {events.DcbTagVersionSequenceTableName} (id, value) values (1, 1)
                        on conflict (id) do update set value = value + 1;

                        """);
    }

    private static void appendInsertHead(ICommandBuilder builder, EventGraph events)
    {
        builder.Append($"insert into {events.DcbTagVersionTableName} (tag_table, tag_value, tenant_id, version)\n");
        builder.Append($"select k.tag_table, k.tag_value, k.tenant_id, (select value from {events.DcbTagVersionSequenceTableName} where id = 1) from (\n");
    }

    // "where true" keeps SQLite from reading the upsert's "on conflict" as part of the select (see the SQLite
    // upsert documentation's parsing ambiguity note).
    private static void appendInsertTail(ICommandBuilder builder, EventGraph events)
        => builder.Append(") as k where true\non conflict (tag_table, tag_value, tenant_id) do update set version = excluded.version;\n");

    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
}
