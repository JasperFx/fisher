using JasperFx.Events.Aggregation;
using Microsoft.EntityFrameworkCore;

namespace Fisher.EntityFrameworkCore;

/// <summary>
///     Registering EF Core as where a projection's documents live (fisher#50).
/// </summary>
public static class EfCoreProjectionExtensions
{
    /// <summary>
    ///     Store <typeparamref name="TDoc" /> as an EF Core entity rather than as a Fisher document, for
    ///     every projection that produces one.
    /// </summary>
    /// <param name="options">The store being configured.</param>
    /// <param name="tableName">
    ///     The physical table <typeparamref name="TContext" /> maps <typeparamref name="TDoc" /> to, so
    ///     a rebuild can clear it.
    /// </param>
    /// <param name="contextFactory">
    ///     Builds a context for one projection batch. It owns its own connection — see the remarks.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         <b>Call this before registering the projection that produces
    ///         <typeparamref name="TDoc" />.</b> Registering a projection maps its document type, and a
    ///         mapped type gets a Fisher table in the migration — so the other order leaves the type
    ///         with two homes. It is checked rather than documented.
    ///     </para>
    ///     <para>
    ///         <b>The context builds on its own connection and is moved onto Fisher's to write</b>, and
    ///         both halves of that are forced rather than chosen. A projection's storage is resolved
    ///         before the batch has opened the connection it will commit on, so there is nothing to
    ///         build against; and the storage reads — every slice loads its current aggregate — long
    ///         before there is a transaction to read in. So the factory takes no connection, and
    ///         <c>UseSqlite(connectionString)</c> is the expected body.
    ///     </para>
    ///     <para>
    ///         What the context must <em>not</em> do is write on that connection. It does not: every
    ///         mutation stays in EF's change tracker until <c>SaveChangesAsync</c>, which runs inside
    ///         Fisher's transaction on Fisher's connection. Two connections writing to one SQLite file
    ///         is the self-deadlock <see cref="DbContextTransactionParticipant{TContext}" /> exists to
    ///         make unreachable, and this is the one shape where a second connection is still correct.
    ///     </para>
    ///     <para>
    ///         <b>The EF table is not created by Fisher's migration.</b> Fisher owns the shape of tables
    ///         it prefixes <c>fi_</c>; an entity's shape is the <c>DbContext</c>'s, so creating it is
    ///         EF's job — an EF migration, or <c>EnsureCreated</c>. The same reasoning that keeps
    ///         <c>CompletelyRemoveAllAsync</c> from dropping EF's tables.
    ///     </para>
    /// </remarks>
    public static void ProjectToEfCore<TDoc, TId, TContext>(this StoreOptions options, string tableName,
        Func<TContext> contextFactory)
        where TDoc : class
        where TId : notnull
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(contextFactory);

        options.Projections.StorageProviders.Register<TDoc, TId>(tableName, (session, tenantId) =>
        {
            var context = contextFactory();

            // One context per batch, enlisted the moment it is created — so whatever the projection
            // does to it lands in the batch's transaction, and a batch that rolls back takes the
            // entities with it.
            session.AddTransactionParticipant(
                DbContextTransactionParticipant<TContext>.MovingOntoFishersConnection(context));

            return new EfCoreProjectionStorage<TDoc, TId, TContext>(context, tenantId);
        });
    }

    /// <summary>
    ///     The <c>DbContext</c> backing this projection's storage, for an <c>Apply</c> method that needs
    ///     to reach beyond its own aggregate.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The escape hatch, and deliberately an extension method on the identity setter JasperFx
    ///         already hands an aggregation projection rather than a base class to derive from. Polecat
    ///         requires <c>EfCoreSingleStreamProjection</c>/<c>EfCoreMultiStreamProjection</c> to reach
    ///         EF at all, which makes every EF-backed projection a different <em>kind</em> of projection;
    ///         here a projection is an ordinary one that happens to be stored in EF, and only a
    ///         projection that genuinely needs the context has to know.
    ///     </para>
    ///     <para>
    ///         Returns null when the projection is not EF-backed — including during live aggregation,
    ///         which folds in memory and has no storage at all.
    ///     </para>
    /// </remarks>
    public static TContext? EfCoreContext<TDoc, TId, TContext>(
        this IIdentitySetter<TDoc, TId> identitySetter)
        where TDoc : class
        where TId : notnull
        where TContext : DbContext
        => identitySetter as EfCoreProjectionStorage<TDoc, TId, TContext> is { } storage
            ? storage.Context
            : null;
}
