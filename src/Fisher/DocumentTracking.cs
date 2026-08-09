namespace Fisher;

/// <summary>
///     What a session remembers about the documents it has loaded and stored (fisher#31).
/// </summary>
/// <remarks>
///     <para>
///         The two behaviours are cumulative — <see cref="DirtyTracking" /> is
///         <see cref="IdentityOnly" /> plus change detection — and both are opt-in, because both cost
///         something per document and neither is worth paying for in a session that loads a document
///         once and forgets it.
///     </para>
///     <para>
///         <b>There is no <c>QueryOnly</c> value, where Marten has one.</b> Marten's names a session
///         that cannot write; Fisher has no such session — <see cref="IQuerySession" /> is the read
///         half of <see cref="IDocumentSession" /> and casting it back yields a working write handle,
///         which <c>DocumentStore.QuerySession</c> says plainly. A tracking mode that resolved the
///         query-only storage flavour would make <c>Store</c> throw on a session the store hands out
///         as writeable, which is a worse answer than the convention Fisher already documents.
///     </para>
/// </remarks>
public enum DocumentTracking
{
    /// <summary>
    ///     Nothing is remembered. Every load reads the database and every write has to be asked for.
    /// </summary>
    None,

    /// <summary>
    ///     An identity map: a document loaded or stored under an identity is handed back as the
    ///     <em>same instance</em> for the rest of the session, and a repeat load does not read the
    ///     database.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         In an embedded store the saved read is small — there is no round trip to save. <b>The
    ///         part that matters is reference identity:</b> without it, a caller who loads the same
    ///         document twice, mutates one copy and stores both gets a last-write-wins outcome that is
    ///         indistinguishable from a lost update.
    ///     </para>
    ///     <para>
    ///         The map covers loads by id <em>and</em> <c>Query&lt;T&gt;()</c>, as Marten's does. Raw
    ///         SQL reads (<c>AdvancedSql</c>) bypass it, because they name their own columns and may
    ///         not select an identity at all.
    ///     </para>
    /// </remarks>
    IdentityOnly,

    /// <summary>
    ///     The identity map, plus change detection: <c>SaveChangesAsync</c> writes every loaded
    ///     document that has changed since it was read, without the caller calling <c>Store</c>.
    /// </summary>
    /// <remarks>
    ///     Costs a serialized snapshot per document read and a re-serialization per document at every
    ///     commit, which is why it is opt-in on every store that offers it. Comparison is by JSON, not
    ///     by <c>Equals</c> — the question is whether the row would change, and a type whose equality
    ///     disagrees with its serialization would answer a different one.
    /// </remarks>
    DirtyTracking
}
