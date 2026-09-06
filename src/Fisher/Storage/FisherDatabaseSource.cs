using JasperFx.Descriptors;
using Weasel.Core.Migrations;

namespace Fisher.Storage;

/// <summary>
///     Adapts Fisher's <see cref="ITenancy" /> to Weasel's <see cref="IDatabaseSource" />, so the
///     <c>db-apply</c> / <c>db-assert</c> / <c>db-patch</c> / <c>db-dump</c> CLI commands discover a
///     Fisher store's database file(s) (fisher#172).
/// </summary>
/// <remarks>
///     <para>
///         Weasel's command line resolves <see cref="IDatabaseSource" /> out of the container
///         (<c>WeaselInput.FilterDatabases</c>). Marten satisfies that because its own <c>ITenancy</c>
///         extends <see cref="IDatabaseSource" /> directly; Fisher's does not, and registering nothing
///         meant every <c>db-*</c> command failed with "No Weasel databases were registered in this
///         application" — which reads as a misconfigured host rather than as an unsupported command,
///         and makes a CI "assert the schema matches" step impossible to write.
///     </para>
///     <para>
///         <b>An adapter rather than widening <see cref="ITenancy" />, following Polecat's reasoning
///         in polecat#501.</b> <see cref="ITenancy" /> is public and implementable outside this repo, so
///         extending it is a breaking change — and it would pull Weasel's migration contract into
///         Fisher's tenancy abstraction for what is purely a command-line concern. Nothing new was
///         needed underneath: <see cref="FisherDatabase" /> already extends Weasel's
///         <c>SqliteDatabase</c> and is therefore already an <see cref="IDatabase" />.
///     </para>
///     <para>
///         <b>The store is resolved lazily, not injected</b>, for the same reason
///         <see cref="FisherSystemPart" /> does it: the <see cref="IConfigureFisher" /> chain has to
///         have run before the tenancy is meaningful, and that only happens on first store resolution.
///     </para>
/// </remarks>
internal sealed class FisherDatabaseSource : IDatabaseSource
{
    private readonly Func<IDocumentStore> _store;

    internal FisherDatabaseSource(Func<IDocumentStore> store)
    {
        _store = store;
    }

    private ITenancy Tenancy => _store().Tenancy;

    public DatabaseCardinality Cardinality => Tenancy.Cardinality;

    /// <remarks>
    ///     A dynamic tenancy is refreshed first — a tenant nothing has resolved yet still has a file to
    ///     migrate, and <c>db-apply</c> silently skipping it is exactly the failure this whole seam
    ///     exists to stop.
    /// </remarks>
    public async ValueTask<IReadOnlyList<IDatabase>> BuildDatabases()
    {
        var tenancy = Tenancy;

        if (tenancy is DynamicTenancy dynamic)
        {
            await dynamic.RefreshAsync().ConfigureAwait(false);
        }

        return tenancy.AllDatabases();
    }

    public async ValueTask<DatabaseUsage> DescribeDatabasesAsync(CancellationToken token)
    {
        var tenancy = Tenancy;
        var databases = await BuildDatabases().ConfigureAwait(false);

        if (tenancy.Cardinality == DatabaseCardinality.Single)
        {
            return new DatabaseUsage
            {
                Cardinality = DatabaseCardinality.Single,
                MainDatabase = databases.Count == 1 ? databases[0].Describe() : tenancy.Default.Describe()
            };
        }

        return new DatabaseUsage
        {
            Cardinality = tenancy.Cardinality,
            Databases = databases.Select(x => x.Describe()).ToList()
        };
    }
}
