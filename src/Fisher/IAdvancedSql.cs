namespace Fisher;

/// <summary>
///     Running your own SQL through a Fisher session and getting typed results back (fisher#34).
/// </summary>
/// <remarks>
///     <para>
///         Reached through <see cref="IQuerySession.AdvancedSql" />. Each type parameter can be a
///         scalar, a JSON-deserializable type read from a single column, or a Fisher document type —
///         see the notes on <see cref="QueryAsync{T}(string,CancellationToken,object[])" /> for what a
///         document costs in columns and where it may appear.
///     </para>
///     <para>
///         Parameters are <c>?</c> placeholders by default, matching
///         <see cref="IDocumentSession.QueueSqlCommand(string,object?[])" />, and each overload has a
///         twin taking a different placeholder character for SQL containing a literal <c>?</c>. Values
///         are converted to Fisher's storage encodings before binding — a Guid to lowercase canonical
///         text, a timestamp to the fixed-width UTC form, a decimal to REAL — because raw SQL is
///         otherwise the one path with no conversion between what a caller holds and what Fisher wrote.
///     </para>
///     <para>
///         This runs on the session's own connection, so it sees the session's uncommitted writes and
///         participates in an open transaction if there is one.
///     </para>
/// </remarks>
public interface IAdvancedSql
{
    /// <summary>
    ///     Run a query and materialize each row as a single <typeparamref name="T" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A scalar</b> (<see cref="int" />, <see cref="string" />, <see cref="Guid" />,
    ///         <see cref="DateTimeOffset" />, <see cref="bool" /> and the rest) consumes one column and
    ///         is read through the provider's typed accessor, so a Guid stored as lowercase canonical
    ///         text and a timestamp stored in Fisher's fixed-width form both come back as the CLR type
    ///         rather than as strings.
    ///     </para>
    ///     <para>
    ///         <b>A registered document type</b> is materialized by the same selector
    ///         <c>session.Query&lt;T&gt;()</c> uses, which is what makes a hierarchy come back as its
    ///         real sub-class and a mapped metadata member get populated. The consequence is that the
    ///         SQL must select that type's columns, in order, at the <em>start</em> of the row —
    ///         <c>select data from fi_doc_angler</c>, or whatever
    ///         <c>SelectFieldsFor&lt;T&gt;()</c> reports. In a tuple query a document type may
    ///         therefore only be the first type parameter; anything else throws, naming the
    ///         restriction.
    ///     </para>
    ///     <para>
    ///         <b>Anything else</b> is deserialized from a single JSON column.
    ///     </para>
    /// </remarks>
    Task<IReadOnlyList<T>> QueryAsync<T>(string sql, CancellationToken token, params object?[] parameters);

    /// <inheritdoc cref="QueryAsync{T}(string,CancellationToken,object?[])" />
    Task<IReadOnlyList<T>> QueryAsync<T>(char placeholder, string sql, CancellationToken token,
        params object?[] parameters);

    /// <summary>
    ///     Run a query whose rows carry two result types, laid out left to right.
    /// </summary>
    Task<IReadOnlyList<(T1, T2)>> QueryAsync<T1, T2>(string sql, CancellationToken token,
        params object?[] parameters);

    /// <inheritdoc cref="QueryAsync{T1,T2}(string,CancellationToken,object?[])" />
    Task<IReadOnlyList<(T1, T2)>> QueryAsync<T1, T2>(char placeholder, string sql, CancellationToken token,
        params object?[] parameters);

    /// <summary>
    ///     Run a query whose rows carry three result types, laid out left to right.
    /// </summary>
    Task<IReadOnlyList<(T1, T2, T3)>> QueryAsync<T1, T2, T3>(string sql, CancellationToken token,
        params object?[] parameters);

    /// <inheritdoc cref="QueryAsync{T1,T2,T3}(string,CancellationToken,object?[])" />
    Task<IReadOnlyList<(T1, T2, T3)>> QueryAsync<T1, T2, T3>(char placeholder, string sql,
        CancellationToken token, params object?[] parameters);

    /// <summary>
    ///     The same query, yielded row by row as the reader produces them.
    /// </summary>
    /// <remarks>
    ///     <b>Streaming runs outside <see cref="StoreOptions.ResiliencePipeline" />, and that is a
    ///     trade rather than an oversight.</b> A retried <c>SQLITE_BUSY</c> re-executes the whole
    ///     delegate, so a live reader handed to a caller would resume against a connection the previous
    ///     attempt had already disposed — the property <c>GetRecentStreamsAsync</c> documents. The
    ///     alternative is to materialize the whole result first, which is not streaming. So a busy
    ///     database surfaces to the caller here where every other Fisher read would have retried it.
    ///     Use <c>QueryAsync</c> unless the result set is large enough that holding it is the problem.
    /// </remarks>
    IAsyncEnumerable<T> StreamAsync<T>(string sql, CancellationToken token, params object?[] parameters);

    /// <inheritdoc cref="StreamAsync{T}(string,CancellationToken,object?[])" />
    IAsyncEnumerable<T> StreamAsync<T>(char placeholder, string sql, CancellationToken token,
        params object?[] parameters);

    /// <inheritdoc cref="StreamAsync{T}(string,CancellationToken,object?[])" />
    IAsyncEnumerable<(T1, T2)> StreamAsync<T1, T2>(string sql, CancellationToken token,
        params object?[] parameters);

    /// <inheritdoc cref="StreamAsync{T}(string,CancellationToken,object?[])" />
    IAsyncEnumerable<(T1, T2)> StreamAsync<T1, T2>(char placeholder, string sql, CancellationToken token,
        params object?[] parameters);

    /// <summary>
    ///     The columns a document type's rows must supply, in order, for
    ///     <see cref="QueryAsync{T}(string,CancellationToken,object?[])" /> to materialize it.
    /// </summary>
    /// <remarks>
    ///     Exists so a caller does not have to guess, and so a test can assert the SQL it writes still
    ///     matches the storage layout after a schema change. Usually just <c>data</c>; a type with
    ///     mapped metadata members or a hierarchy discriminator has more.
    /// </remarks>
    string[] SelectFieldsFor<T>() where T : notnull;
}
