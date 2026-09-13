using JasperFx.MultiTenancy;

namespace Fisher.Storage;

/// <summary>
///     fisher#257 — refuses a store where two tenants share a database file and nothing in that file
///     can tell them apart.
/// </summary>
/// <remarks>
///     <para>
///         <b>Sharded tenancy is a real configuration</b> — several tenants co-located in one file,
///         with more than one file — and fisher#252 made it work correctly. What makes it work is a
///         <c>tenant_id</c> the storage can filter on: conjoined event tenancy puts one in the primary
///         key of <c>fi_streams</c> and <c>fi_events</c>, and <c>MultiTenanted()</c> puts one on a
///         document table. <b>Without them the tenants are not sharing a file — they are one tenant
///         with two names</b>, writing the same rows, and the configuration says otherwise.
///     </para>
///     <para>
///         <b>It is silent in the worst direction</b>, which is the whole reason for a refusal rather
///         than a note. Every read returns plausible data, each tenant sees a superset that looks like
///         its own plus some, and the first visible symptom is one tenant's row turning up in another's
///         report. Same shape as fisher#51 — a cross-tenant read where the tenant owning most of the
///         data sees a correct-looking answer with extras and the tenant with none sees somebody
///         else's. This is the <c>AddFisherStore&lt;T&gt;</c> precedent one layer down (fisher#46):
///         two stores over one file with the same <c>DatabaseSchemaName</c> are refused for exactly
///         this reason.
///     </para>
///     <para>
///         <b>⚠️ Conjoined event tenancy is required unconditionally, even for a store that never
///         appends an event, and that is a decision rather than an oversight.</b> The conditional rule
///         — require it only when the store uses events — cannot be decided honestly: the event tables
///         are created by every migration whether or not anything writes to them, and an append does
///         not need its event type registered, so "does this store use events" has no reliable answer
///         at configuration time. A rule that guessed would refuse some safe stores and admit some
///         unsafe ones. The unconditional rule costs a documents-only sharded store one line and an
///         unused column, and it is teachable: <em>sharing a file means conjoined</em>.
///     </para>
///     <para>
///         <b>Two checkpoints, because one cannot be complete.</b>
///         <see cref="AssertTenantsSharingAFileCanBeToldApart" /> runs in <c>DocumentStore</c>'s
///         constructor and covers everything registered at configuration time, which is the common
///         case and the one worth reporting early by name. Document mappings are created lazily,
///         though, so a type nothing registered is invisible there —
///         <see cref="AssertDocumentTypeCanBeToldApart" /> runs where such a type first becomes real,
///         at table provisioning, beside the <c>AutoCreate.None</c> check that is already there.
///     </para>
///     <para>
///         <b>The check cannot live in <c>DocumentSchema.MappingFor</c></b>, which is the placement
///         that suggests itself and is wrong: <c>Schema.For&lt;T&gt;().MultiTenanted()</c> creates the
///         mapping and <em>then</em> sets the flag, so a refusal at creation would fire before the line
///         that satisfies it could run. The same trap fisher#218 had to move
///         <c>AssertEveryMappingHasIdentity</c> out of <c>DocumentMapping</c>'s constructor for.
///     </para>
/// </remarks>
internal static class SharedFileTenancyGuard
{
    /// <summary>
    ///     The configuration-time check: every shared file, against the event store and every document
    ///     type the schema has mapped so far.
    /// </summary>
    internal static void AssertTenantsSharingAFileCanBeToldApart(StoreOptions options, ITenancy tenancy)
    {
        var shared = tenancy.SharedFiles();

        if (shared.Count == 0)
        {
            return;
        }

        foreach (var file in shared)
        {
            AssertEventsCanBeToldApart(options, file);

            foreach (var mapping in options.Schema.AllMappings())
            {
                AssertDocumentTypeCanBeToldApart(mapping, file);
            }
        }
    }

    /// <summary>
    ///     The first-use check for one document type, for a mapping the configuration-time sweep could
    ///     not have seen.
    /// </summary>
    /// <remarks>
    ///     Cheap enough to run per provisioning: the list is computed once when the tenancy is built
    ///     and stashed on <see cref="StoreOptions.SharedTenantFiles" />, so an ordinary store pays a
    ///     field read and a count.
    /// </remarks>
    internal static void AssertDocumentTypeCanBeToldApart(IReadOnlyList<SharedTenantFile> shared,
        DocumentMapping mapping)
    {
        foreach (var file in shared)
        {
            AssertDocumentTypeCanBeToldApart(mapping, file);
        }
    }

    private static void AssertEventsCanBeToldApart(StoreOptions options, SharedTenantFile file)
    {
        if (options.Events.TenancyStyle == TenancyStyle.Conjoined)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Tenants {Name(file)} are configured to share the database file '{file.DataSource}', but the "
            + "event store is not conjoined — fi_streams and fi_events have no tenant_id to tell them "
            + "apart, so those tenants would write the same rows and read each other's. Set "
            + "options.Events.TenancyStyle = TenancyStyle.Conjoined to share a file, or give each tenant "
            + "a connection string of its own. See fisher#257.");
    }

    private static void AssertDocumentTypeCanBeToldApart(DocumentMapping mapping, SharedTenantFile file)
    {
        if (mapping.IsConjoined)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Tenants {Name(file)} are configured to share the database file '{file.DataSource}', but the "
            + $"document type '{mapping.DocumentType.FullName}' is not multi-tenanted — its table has no "
            + "tenant_id, so one row per id is all those tenants get between them and each would "
            + "overwrite and read the others'. Declare it with "
            + $"options.Schema.For<{mapping.DocumentType.Name}>().MultiTenanted(), or with "
            + "[MultiTenanted] on the type, or give each tenant a connection string of its own. See "
            + "fisher#257.");
    }

    private static string Name(SharedTenantFile file)
        => string.Join(", ", file.TenantIds.Select(x => $"'{x}'"));
}
