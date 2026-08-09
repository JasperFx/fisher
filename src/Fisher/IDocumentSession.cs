using System.Linq.Expressions;
using Fisher.Events;

namespace Fisher;

/// <summary>
///     A read-only Fisher session.
/// </summary>
/// <remarks>
///     <b>Read-only here is a convention rather than a guarantee.</b> Fisher has no query-only session
///     type — this is the read half of <see cref="IDocumentSession" />, and every session the store
///     hands out implements both — so an injected <see cref="IQuerySession" /> can be cast back to a
///     working write handle. A separate type would cost a connection per scope to express a
///     distinction the store does not make. Declare this where a piece of code only reads, to say so;
///     do not rely on it to stop code that means otherwise.
/// </remarks>
public interface IQuerySession : IAsyncDisposable, IDisposable
{
    /// <summary>
    ///     The tenant every operation in this session is scoped to.
    /// </summary>
    string TenantId { get; }

    /// <summary>
    ///     Start a LINQ query over a document type.
    /// </summary>
    /// <remarks>
    ///     Terminal operators are the async ones in <see cref="Linq.QueryableExtensions" /> —
    ///     <c>ToListAsync</c> and friends. Synchronous enumeration throws rather than blocking on the
    ///     async path; see <see cref="Linq.FisherQueryable{T}.GetEnumerator" />.
    /// </remarks>
    IQueryable<T> Query<T>() where T : notnull;

    /// <summary>
    ///     Load a document by its identity, or null when there is none.
    /// </summary>
    Task<T?> LoadAsync<T>(Guid id, CancellationToken token = default) where T : class;

    /// <inheritdoc cref="LoadAsync{T}(Guid,CancellationToken)" />
    Task<T?> LoadAsync<T>(string id, CancellationToken token = default) where T : class;

    /// <inheritdoc cref="LoadAsync{T}(Guid,CancellationToken)" />
    Task<T?> LoadAsync<T>(int id, CancellationToken token = default) where T : class;

    /// <inheritdoc cref="LoadAsync{T}(Guid,CancellationToken)" />
    Task<T?> LoadAsync<T>(long id, CancellationToken token = default) where T : class;

    /// <summary>
    ///     Load a document by a strong-typed identity, or null when there is none.
    /// </summary>
    /// <remarks>
    ///     Both type parameters are explicit, which is what keeps this from being ambiguous with the
    ///     four single-parameter overloads above. Use it for a document whose identity is a wrapper —
    ///     <c>LoadAsync&lt;Payment, PaymentId&gt;(id)</c>. The four canonical types are reachable
    ///     through here too, and reach exactly the same code.
    /// </remarks>
    Task<T?> LoadAsync<T, TId>(TId id, CancellationToken token = default)
        where T : class where TId : notnull;

    /// <summary>
    ///     Run your own SQL through this session and get typed results back.
    /// </summary>
    /// <remarks>
    ///     On the session's own connection, so it sees the session's uncommitted writes and joins an
    ///     open transaction. The write counterpart is
    ///     <see cref="IDocumentSession.QueueSqlCommand(string,object?[])" />.
    /// </remarks>
    IAdvancedSql AdvancedSql { get; }

    /// <summary>
    ///     Whether a document with this identity exists, without materializing it.
    /// </summary>
    /// <remarks>
    ///     <c>select 1 … limit 1</c> through the storage, so it carries the same tenant, soft-delete
    ///     and hierarchy filters <see cref="LoadAsync{T}(Guid,CancellationToken)" /> does. The
    ///     alternative — <c>LoadAsync(id) is not null</c> — reads and deserializes a whole document to
    ///     learn a boolean.
    /// </remarks>
    Task<bool> CheckExistsAsync<T>(Guid id, CancellationToken token = default) where T : class;

    /// <inheritdoc cref="CheckExistsAsync{T}(Guid,CancellationToken)" />
    Task<bool> CheckExistsAsync<T>(string id, CancellationToken token = default) where T : class;

    /// <inheritdoc cref="CheckExistsAsync{T}(Guid,CancellationToken)" />
    Task<bool> CheckExistsAsync<T>(int id, CancellationToken token = default) where T : class;

    /// <inheritdoc cref="CheckExistsAsync{T}(Guid,CancellationToken)" />
    Task<bool> CheckExistsAsync<T>(long id, CancellationToken token = default) where T : class;

    /// <summary>
    ///     A document's stored JSON, exactly as it was written, or null when there is none.
    /// </summary>
    /// <remarks>
    ///     Byte-exact: <c>data</c> is TEXT holding what System.Text.Json produced, so nothing
    ///     normalises whitespace or key order on the way out. Carries the same tenant, soft-delete and
    ///     hierarchy filters the typed load does.
    /// </remarks>
    Task<string?> LoadJsonAsync<T>(Guid id, CancellationToken token = default) where T : class;

    /// <inheritdoc cref="LoadJsonAsync{T}(Guid,CancellationToken)" />
    Task<string?> LoadJsonAsync<T>(string id, CancellationToken token = default) where T : class;

    /// <inheritdoc cref="LoadJsonAsync{T}(Guid,CancellationToken)" />
    Task<string?> LoadJsonAsync<T>(int id, CancellationToken token = default) where T : class;

    /// <inheritdoc cref="LoadJsonAsync{T}(Guid,CancellationToken)" />
    Task<string?> LoadJsonAsync<T>(long id, CancellationToken token = default) where T : class;

    /// <summary>
    ///     What a document's metadata columns hold, without loading the document (fisher#29). Null when
    ///     there is no such row.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The counterpart to mapping a column onto a member: mapping is for when the document
    ///         wants to carry the value, this is for when a caller wants to ask. It reads only the
    ///         columns the type actually has — an unenabled one comes back null rather than being
    ///         selected and failing.
    ///     </para>
    ///     <para>
    ///         <b>A soft-deleted document has metadata, and this returns it.</b> The read carries the
    ///         tenant scope but deliberately not the soft-delete filter, since "was this deleted, and
    ///         when" is one of the questions it exists to answer and no ordinary load can.
    ///     </para>
    /// </remarks>
    Task<Metadata.StoredDocumentMetadata?> MetadataForAsync<T>(T document, CancellationToken token = default)
        where T : notnull;

    /// <inheritdoc cref="MetadataForAsync{T}(T,CancellationToken)" />
    Task<Metadata.StoredDocumentMetadata?> MetadataForAsync<T>(Guid id, CancellationToken token = default)
        where T : notnull;

    /// <inheritdoc cref="MetadataForAsync{T}(T,CancellationToken)" />
    Task<Metadata.StoredDocumentMetadata?> MetadataForAsync<T>(string id, CancellationToken token = default)
        where T : notnull;

    /// <inheritdoc cref="MetadataForAsync{T}(T,CancellationToken)" />
    Task<Metadata.StoredDocumentMetadata?> MetadataForAsync<T>(int id, CancellationToken token = default)
        where T : notnull;

    /// <inheritdoc cref="MetadataForAsync{T}(T,CancellationToken)" />
    Task<Metadata.StoredDocumentMetadata?> MetadataForAsync<T>(long id, CancellationToken token = default)
        where T : notnull;

    /// <summary>
    ///     Run a query plan against this session.
    /// </summary>
    Task<T> QueryByPlanAsync<T>(Batching.IQueryPlan<T> plan, CancellationToken token = default);

    /// <summary>
    ///     The SQL a query would run, for diagnostics.
    /// </summary>
    /// <remarks>
    ///     Parameter values are not inlined — the text carries the parameter names the command would
    ///     bind, so it is readable rather than executable. Useful in a test to assert that an index is
    ///     reachable, or that a filter Fisher adds implicitly is actually there.
    /// </remarks>
    string ToSql<T>(IQueryable<T> queryable) where T : notnull;

    /// <summary>
    ///     Load several documents by identity. Missing ids are absent from the result rather than
    ///     null entries, so it is not necessarily as long as the input.
    /// </summary>
    Task<IReadOnlyList<T>> LoadManyAsync<T>(params Guid[] ids) where T : class;

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    Task<IReadOnlyList<T>> LoadManyAsync<T>(params string[] ids) where T : class;

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    Task<IReadOnlyList<T>> LoadManyAsync<T>(params int[] ids) where T : class;

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    Task<IReadOnlyList<T>> LoadManyAsync<T>(params long[] ids) where T : class;
}

/// <summary>
///     A writable Fisher session: a unit of work over the event store, flushed by
///     <see cref="SaveChangesAsync" />.
/// </summary>
/// <remarks>
///     The <see cref="JasperFx.Events.IStorageOperations" /> half is what lets Fisher's session types
///     close JasperFx's aggregation and projection generics, which constrain the write session to be
///     both the read session and a storage-operations surface. Its members are the projection write
///     path — see <c>Fisher.Internal.FisherSession</c> for which of them are live today.
/// </remarks>
public interface IDocumentSession : IQuerySession, JasperFx.Events.IStorageOperations
{
    /// <summary>
    ///     The event store write surface for this session.
    /// </summary>
    EventOperations Events { get; }

    /// <summary>
    ///     The correlation id stamped onto everything this unit of work writes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Seeded from <c>Activity.Current.RootId</c> when the session is created, so tracing
    ///         context reaches the store with no application code; assigning here wins over that.
    ///     </para>
    ///     <para>
    ///         <b>One value, two destinations.</b> It reaches appended events when
    ///         <c>StoreOptions.Events.EnableCorrelationId</c> is on, and documents whose type enabled the
    ///         <c>correlation_id</c> metadata column (fisher#29) — so a document and an event written in
    ///         the same unit of work carry the same value, because there is only one source for it.
    ///     </para>
    /// </remarks>
    string? CorrelationId { get; set; }

    /// <inheritdoc cref="CorrelationId" />
    /// <remarks>Seeded from <c>Activity.Current.ParentId</c>.</remarks>
    string? CausationId { get; set; }

    /// <inheritdoc cref="CorrelationId" />
    /// <remarks>Not seeded from anything — an application that wants it sets it.</remarks>
    string? CurrentUserName { get; set; }

    /// <inheritdoc cref="CorrelationId" />
    Dictionary<string, object>? Headers { get; }

    /// <inheritdoc cref="CorrelationId" />
    void SetHeader(string key, object value);

    /// <summary>
    ///     Queue a document to be written on the next <see cref="SaveChangesAsync" />, inserting or
    ///     updating as needed.
    /// </summary>
    void Store<T>(T document) where T : notnull;

    /// <summary>
    ///     Queue a document to be written, requiring the stored row's revision to be below
    ///     <paramref name="revision" />.
    /// </summary>
    /// <remarks>
    ///     For a document type configured for numeric revisions — by implementing
    ///     <see cref="JasperFx.IRevisioned" /> or through <c>Schema.For&lt;T&gt;().UseNumericRevisions()</c>.
    ///     A miss fails the whole unit of work with a <c>ConcurrencyException</c>; use
    ///     <see cref="TryUpdateRevision{T}" /> to drop the stale write instead.
    /// </remarks>
    void Store<T>(T document, int revision) where T : notnull;

    /// <inheritdoc cref="Store{T}(T,int)" />
    void UpdateRevision<T>(T document, int revision) where T : notnull;

    /// <summary>
    ///     Queue a document to be written at an explicit revision, dropping the write rather than
    ///     failing the unit of work if the stored row has already moved past it.
    /// </summary>
    void TryUpdateRevision<T>(T document, int revision) where T : notnull;

    /// <inheritdoc cref="Store{T}(T)" />
    void Store<T>(params T[] documents) where T : notnull;

    /// <summary>
    ///     Queue a document to be inserted, failing at commit if one with that identity already exists.
    /// </summary>
    void Insert<T>(T document) where T : notnull;

    /// <summary>
    ///     Queue a document to be updated, failing at commit if no document with that identity exists.
    /// </summary>
    void Update<T>(T document) where T : notnull;

    /// <summary>
    ///     Queue a document for deletion. A soft-deleted type is flagged rather than removed.
    /// </summary>
    void Delete<T>(T document) where T : notnull;

    /// <summary>
    ///     Queue the document with this identity for deletion, whether or not it has been loaded.
    /// </summary>
    void Delete<T>(Guid id) where T : notnull;

    /// <inheritdoc cref="Delete{T}(Guid)" />
    void Delete<T>(string id) where T : notnull;

    /// <inheritdoc cref="Delete{T}(Guid)" />
    void Delete<T>(int id) where T : notnull;

    /// <inheritdoc cref="Delete{T}(Guid)" />
    void Delete<T>(long id) where T : notnull;

    /// <summary>
    ///     Queue a document to be removed outright, even if its type is soft-deleted. Identical to
    ///     <see cref="Delete{T}(T)" /> for every other type.
    /// </summary>
    void HardDelete<T>(T document) where T : notnull;

    /// <inheritdoc cref="HardDelete{T}(T)" />
    void HardDelete<T>(Guid id) where T : notnull;

    /// <inheritdoc cref="HardDelete{T}(T)" />
    void HardDelete<T>(string id) where T : notnull;

    /// <inheritdoc cref="HardDelete{T}(T)" />
    void HardDelete<T>(int id) where T : notnull;

    /// <inheritdoc cref="HardDelete{T}(T)" />
    void HardDelete<T>(long id) where T : notnull;

    /// <summary>
    ///     Queue every document matching the predicate for deletion, without loading any of them. A
    ///     soft-deleted type is flagged rather than removed.
    /// </summary>
    /// <remarks>
    ///     The predicate is translated by the same LINQ layer <see cref="IQuerySession.Query{T}" />
    ///     uses, so it supports what that supports and refuses the rest by name.
    /// </remarks>
    void DeleteWhere<T>(Expression<Func<T, bool>> predicate) where T : notnull;

    /// <summary>
    ///     Queue every document matching the predicate to be removed outright, even if its type is
    ///     soft-deleted.
    /// </summary>
    void HardDeleteWhere<T>(Expression<Func<T, bool>> predicate) where T : notnull;

    /// <summary>
    ///     Bring every soft-deleted document matching the predicate back. Throws for a type that is not
    ///     soft-deleted, where there is nothing to bring back.
    /// </summary>
    void UndoDeleteWhere<T>(Expression<Func<T, bool>> predicate) where T : notnull;

    /// <summary>
    ///     Change part of a stored document without loading it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every operation becomes one json1 function inside a single <c>update</c>, and several
    ///         calls on the returned expression nest into one statement. Committed with everything else
    ///         in the unit of work.
    ///     </para>
    ///     <para>
    ///         It avoids the deserialize/mutate/serialize round trip, <b>not</b> the row rewrite —
    ///         <c>json_set</c> re-renders the document, so a patched row is no longer byte-identical to
    ///         what the serializer would have written and a new or renamed key lands at the end.
    ///     </para>
    /// </remarks>
    Patching.IPatchExpression<T> Patch<T>(Guid id) where T : notnull;

    /// <inheritdoc cref="Patch{T}(Guid)" />
    Patching.IPatchExpression<T> Patch<T>(string id) where T : notnull;

    /// <inheritdoc cref="Patch{T}(Guid)" />
    Patching.IPatchExpression<T> Patch<T>(int id) where T : notnull;

    /// <inheritdoc cref="Patch{T}(Guid)" />
    Patching.IPatchExpression<T> Patch<T>(long id) where T : notnull;

    /// <summary>
    ///     Patch every document matching a predicate.
    /// </summary>
    /// <remarks>
    ///     The predicate goes through the same LINQ layer <c>Query&lt;T&gt;()</c> uses, and is applied
    ///     last — after the tenant scope and the soft-delete guard — because a compound predicate is
    ///     parenthesised and so cannot swallow them.
    /// </remarks>
    Patching.IPatchExpression<T> Patch<T>(Expression<Func<T, bool>> predicate) where T : notnull;

    /// <summary>
    ///     Queue arbitrary SQL to run in this unit of work's transaction, alongside the documents and
    ///     events.
    /// </summary>
    /// <param name="sql">
    ///     The statement, with <c>?</c> for each parameter. A trailing semicolon is trimmed.
    /// </param>
    /// <param name="parameterValues">
    ///     One value per <c>?</c>, in order. A count mismatch throws at commit, naming both counts.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         <b>This is worth more on SQLite than the same method is on Marten or Polecat.</b> An
    ///         application using Fisher keeps its own tables in the same file, and SQLite permits one
    ///         writer per file — so without this, writing your rows and Fisher's atomically is not
    ///         merely inconvenient, it means taking the write lock twice and contending with yourself.
    ///     </para>
    ///     <para>
    ///         Values are converted to the encodings Fisher stores before binding: a <see cref="Guid" />
    ///         to its lowercase canonical text, a <see cref="DateTimeOffset" /> or <see cref="DateTime" />
    ///         to Fisher's fixed-width UTC form, a <see cref="decimal" /> to REAL. Bound raw, each of the
    ///         three matches nothing Fisher has written — silently. Everything else binds unchanged.
    ///     </para>
    ///     <para>
    ///         Statements run in the order they were queued, interleaved with nothing — document and
    ///         event operations queued before and after keep their relative order too.
    ///     </para>
    /// </remarks>
    void QueueSqlCommand(string sql, params object?[] parameterValues);

    /// <summary>
    ///     <see cref="QueueSqlCommand(string,object?[])" /> with a placeholder character other than
    ///     <c>?</c>, for SQL that contains a literal one.
    /// </summary>
    /// <remarks>
    ///     The placeholder is found by splitting the text, so a <c>?</c> inside a string literal or a
    ///     JSON path would otherwise be read as a parameter and the counts would not add up. Polecat
    ///     offers the same escape on <c>IAdvancedSql</c> rather than here; Fisher offers it in both
    ///     places, because a JSON path is a much more likely thing to write against `fi_doc_*` than
    ///     against a relational table.
    /// </remarks>
    void QueueSqlCommand(char placeholder, string sql, params object?[] parameterValues);

    /// <summary>
    ///     Remove a document from this session's identity map, its change tracking and its queued
    ///     writes (fisher#31).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Nothing reaches the database: a document already committed stays committed. What this
    ///         undoes is everything the session still holds about it — including a <c>Store</c> that has
    ///         not been saved yet, which is the one thing there was no other way to take back.
    ///     </para>
    ///     <para>
    ///         Matching is by reference, so ejecting one instance leaves a different instance of the
    ///         same document alone.
    ///     </para>
    /// </remarks>
    void Eject<T>(T document) where T : notnull;

    /// <summary>
    ///     <see cref="Eject{T}" /> for every document of a type, including sub-classes of it in a
    ///     document hierarchy.
    /// </summary>
    void EjectAllOfType(Type type);

    /// <summary>
    ///     Abandon this unit of work: every queued document write, deletion, raw SQL command, appended
    ///     event and DCB boundary is dropped, and nothing is written.
    /// </summary>
    /// <remarks>
    ///     The identity map survives — this abandons pending changes, not what the session has read.
    ///     Change trackers do not, because a tracker <em>is</em> a pending change that has not been
    ///     asked for yet.
    /// </remarks>
    void EjectAllPendingChanges();

    /// <summary>
    ///     Commit every queued operation in a single transaction.
    /// </summary>
    Task SaveChangesAsync(CancellationToken token = default);
}
