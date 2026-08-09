using Fisher.Events;

namespace Fisher;

/// <summary>
///     Document and event operations scoped to a tenant other than the session's own, queueing into
///     the session's unit of work (fisher#33). Returned by
///     <see cref="IDocumentSession.ForTenant" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>It is a scope, not a session.</b> There is no <c>SaveChangesAsync</c>, no connection and
///         no transaction of its own: everything it queues commits when the parent commits, in the
///         parent's single <c>BEGIN IMMEDIATE</c>. That is the whole feature — an admin operation
///         seeding several tenants, or a fan-out writing one source stream into per-tenant documents,
///         as one atomic write.
///     </para>
///     <para>
///         Reads through it are scoped to its tenant too, so <c>LoadAsync</c> and
///         <c>Query&lt;T&gt;()</c> answer about the other tenant rather than about the parent's.
///     </para>
///     <para>
///         <b>Only conjoined-tenanted types can be reached through it.</b> A type registered without
///         <c>MultiTenanted()</c> has no <c>tenant_id</c> column, so writing it "for another tenant"
///         would write the one unscoped table and look like it worked — the same class of silent
///         cross-tenant answer fisher#51 was. Every operation on a single-tenant type is refused by
///         name instead.
///     </para>
/// </remarks>
public interface ITenantOperations : IDocumentOperations
{
    /// <summary>The tenant this scope reads and writes as.</summary>
    new string TenantId { get; }

    /// <summary>The session whose unit of work this scope queues into.</summary>
    IDocumentSession Parent { get; }

    /// <summary>Event store operations scoped to <see cref="TenantId" />.</summary>
    new EventOperations Events { get; }
}
