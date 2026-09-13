namespace Fisher.Projections.Vectors;

/// <summary>
///     The document shape a <see cref="VectorProjection{TDoc,TId}" /> writes (fisher#261).
/// </summary>
/// <typeparam name="TId">
///     The document's identity. <b>Open rather than fixed to <see cref="System.Guid" /></b>, which is
///     the first place Marten's template does not port: <c>Marten.PgVector</c>'s projection hardcodes
///     <c>Guid</c> at every layer, so a string-identified store cannot use it at all. Fisher stores
///     four id types plus strong-typed wrappers, and this closes over whichever one the document has.
/// </typeparam>
/// <remarks>
///     <para>
///         A marker interface rather than reflection over the mapping, following
///         <c>ISoftDeleted</c> / <c>IVersioned</c> / <c>IRevisioned</c>: the projection has to
///         <em>write</em> all four members, and a compiled setter per member would be machinery
///         standing in for three lines a document already wants to declare.
///     </para>
///     <para>
///         <b><see cref="Content" /> is stored as well as hashed</b>, as Marten's table does. It costs
///         storage and it is what makes a re-embedding possible — changing the model or the dimension
///         count means embedding the same text again, and without the text that means replaying the
///         stream.
///     </para>
/// </remarks>
public interface IVectorized<TId>
{
    /// <summary>The document's identity, assigned from the mapping's id selector.</summary>
    TId Id { get; set; }

    /// <summary>The text that was embedded.</summary>
    string? Content { get; set; }

    /// <summary>
    ///     SHA-256 of <see cref="Content" />, compared before the provider is called.
    /// </summary>
    /// <remarks>
    ///     This is what makes re-projecting cheap: an event whose content is unchanged costs a read
    ///     and no embedding call at all. Embedding is the expensive, usually metered, part.
    /// </remarks>
    string? ContentHash { get; set; }

    /// <summary>
    ///     The embedding itself — and the member the document's <c>VectorIndex</c> must be declared
    ///     on, which <see cref="VectorProjection{TDoc,TId}" /> asserts at configuration time.
    /// </summary>
    float[]? Embedding { get; set; }
}
