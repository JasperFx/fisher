using System.Security.Cryptography;
using System.Text;
using Fisher.Internal;
using Fisher.Linq;
using JasperFx.Events;
using JasperFx.Events.Vectors;

namespace Fisher.Projections.Vectors;

/// <summary>
///     A projection that turns events into embeddings (fisher#261) — the half of vector search that
///     <em>produces</em> a vector, where <c>VectorSearchAsync</c> (fisher#241) searches one the
///     application already put on the document.
/// </summary>
/// <remarks>
///     <para>
///         Ported in shape from <c>Marten.PgVector.Projection.VectorProjection</c>: map event types to
///         text, hash the text, skip re-embedding when the hash is unchanged, upsert by the mapped id.
///         <b>Four things are deliberately different, and all four come from defects in that
///         template rather than from SQLite</b> — see each below.
///     </para>
///     <para>
///         <b>⚠️ It writes an ordinary Fisher document, not a table of its own.</b> That is what makes
///         the result searchable with nothing added: the document declares
///         <c>VectorIndex(x =&gt; x.Embedding, dimensions)</c> and <c>VectorSearchAsync</c> and
///         <c>HybridSearchAsync</c> read it like any other. It also means the migration, soft delete,
///         tenancy and the identity map all apply without this class knowing about any of them.
///     </para>
///     <para>
///         <b>Asynchronous only.</b> Embedding is a network call per batch, and an inline projection
///         runs inside the caller's <c>SaveChangesAsync</c> — holding SQLite's single write lock open
///         across an HTTP round trip to an embedding API, which would block every other writer in the
///         process for the duration. Marten's refuses inline too, for the weaker reason that it needs
///         a connection of its own.
///     </para>
/// </remarks>
/// <typeparam name="TDoc">The document written. Must carry the four members of <see cref="IVectorized{TId}" />.</typeparam>
/// <typeparam name="TId">The document's identity — any type Fisher can store, including a strong-typed wrapper.</typeparam>
public abstract class VectorProjection<TDoc, TId> : IProjection
    where TDoc : class, IVectorized<TId>, new()
    where TId : notnull
{
    private readonly IEmbeddingProvider _provider;
    private readonly VectorProjectionMap<TDoc, TId> _map = new();

    protected VectorProjection(IEmbeddingProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

        Configure(_map);

        if (_map.IsEmpty)
        {
            throw new InvalidOperationException(
                $"'{GetType().Name}' configured no event mappings, so it would read every page and "
                + "write nothing. Call map.Map<TEvent>(content, id) in Configure for at least one "
                + "event type.");
        }
    }

    /// <summary>Declare which events produce content, and which delete.</summary>
    protected abstract void Configure(VectorProjectionMap<TDoc, TId> map);

    /// <summary>The provider's dimension count, for asserting the declared index agrees with it.</summary>
    internal int Dimensions => _provider.Dimensions;

    public async Task ApplyAsync(IDocumentSession operations, IReadOnlyList<IEvent> events,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
        {
            return;
        }

        AssertTheIndexAgrees(operations);

        // Last write wins within a page, which is what makes a stream that edits the same content
        // twice cost one embedding rather than two. Deletes are kept in order against the writes so a
        // create-then-delete inside one page ends deleted.
        var pending = new Dictionary<TId, string?>();

        foreach (var @event in events)
        {
            if (_map.TryDelete(@event, out var deletedId))
            {
                pending[deletedId] = null;
                continue;
            }

            if (_map.TryContent(@event, out var id, out var content))
            {
                // A selector returning null means "this event carries no content", which is a real
                // answer and distinct from a selector that FAILED -- see VectorProjectionMap.
                if (content is null)
                {
                    continue;
                }

                pending[id] = content;
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        foreach (var (id, _) in pending.Where(x => x.Value is null))
        {
            // Addressed by the id the MAP produced, never by the stream id -- see VectorProjectionMap.
            operations.Delete(new TDoc { Id = id });
        }

        var writes = pending.Where(x => x.Value is not null)
            .Select(x => (Id: x.Key, Content: x.Value!, Hash: Sha256(x.Value!)))
            .ToList();

        if (writes.Count == 0)
        {
            return;
        }

        var unchanged = await UnchangedAsync(operations, writes, cancellation).ConfigureAwait(false);
        var stale = writes.Where(x => !unchanged.Contains(x.Id)).ToList();

        if (stale.Count == 0)
        {
            return;
        }

        // One provider call for the batch, not one per row. Embedding is the metered part, and a
        // page of a hundred documents is a hundred round trips the other way.
        var vectors = await _provider
            .GenerateEmbeddingsAsync(stale.Select(x => x.Content).ToArray(), cancellation)
            .ConfigureAwait(false);

        if (vectors.Length != stale.Count)
        {
            throw new InvalidOperationException(
                $"{_provider.GetType().FullName} returned {vectors.Length} embeddings for "
                + $"{stale.Count} texts. The contract is one vector per input, in order — a provider "
                + "that drops or reorders them would pair every embedding with the wrong document "
                + "from that point on, silently.");
        }

        for (var i = 0; i < stale.Count; i++)
        {
            var (id, content, hash) = stale[i];

            operations.Store(new TDoc
            {
                Id = id,
                Content = content,
                ContentHash = hash,
                Embedding = vectors[i].ToArray()
            });
        }
    }

    /// <summary>
    ///     Which of these ids already hold exactly this content.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Read through the session rather than on a connection of its own</b>, which is the
    ///         first divergence from Marten's template and the one with teeth. Marten's projection
    ///         opens <c>database.CreateConnection()</c> and does its reads and writes outside the
    ///         session's transaction, so an embedding commits even when the events that produced it
    ///         roll back. Here everything is queued onto the session the projection batch hands over,
    ///         so the embedding row and the progression row land in one transaction — and on SQLite a
    ///         second connection writing while the batch holds the file's write lock would block
    ///         against itself anyway.
    ///     </para>
    ///     <para>
    ///         One query for the page rather than one per id, through the ordinary LINQ path, so the
    ///         tenant and soft-delete filters apply without being restated.
    ///     </para>
    /// </remarks>
    private static async Task<HashSet<TId>> UnchangedAsync(IDocumentSession operations,
        IReadOnlyList<(TId Id, string Content, string Hash)> writes, CancellationToken cancellation)
    {
        var byId = writes.ToDictionary(x => x.Id, x => x.Hash);
        var ids = byId.Keys.ToList();

        // IsOneOf rather than ids.Contains(x.Id): an array receiver binds to
        // MemoryExtensions.Contains(ReadOnlySpan<T>, T), whose span operand cannot be unwrapped when
        // T is an open type parameter — a ref struct cannot be returned as object. IsOneOf is the
        // marker operator built for this (fisher#26) and reads the same either way.
        var existing = await operations.Query<TDoc>()
            .Where(x => x.Id.IsOneOf(ids))
            .ToListAsync(cancellation)
            .ConfigureAwait(false);

        var unchanged = new HashSet<TId>();

        foreach (var document in existing)
        {
            if (document.ContentHash is { } hash
                && byId.TryGetValue(document.Id, out var incoming)
                && string.Equals(hash, incoming, StringComparison.Ordinal))
            {
                unchanged.Add(document.Id);
            }
        }

        return unchanged;
    }

    /// <summary>
    ///     The declared vector index has to be on <see cref="IVectorized{TId}.Embedding" />, and its
    ///     dimensions have to match the provider's.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Both halves fail silently otherwise, in the way this codebase keeps meeting. An index
    ///         declared on some other member leaves the projection writing one column and every search
    ///         reading another — valid SQL, no error, and an index that is always empty. A dimension
    ///         mismatch is worse: the rows are written, and every search refuses at the
    ///         <em>caller's</em> end with a message about the query vector, which points away from the
    ///         projection that produced them.
    ///     </para>
    ///     <para>
    ///         Checked on the first page rather than at construction because the projection is built
    ///         before the store is, so there is no schema to ask yet.
    ///     </para>
    /// </remarks>
    private void AssertTheIndexAgrees(IDocumentSession operations)
    {
        var options = ((FisherSession)operations).Options;
        var mapping = options.Schema.MappingFor(typeof(TDoc));
        var indexes = mapping.VectorIndexes;

        var index = indexes.FirstOrDefault(x => x.MemberName == nameof(IVectorized<TId>.Embedding));

        if (index is null)
        {
            throw new InvalidOperationException(
                $"'{typeof(TDoc).Name}' declares no vector index on '{nameof(IVectorized<TId>.Embedding)}', so "
                + $"'{GetType().Name}' would write a column nothing searches. Declare "
                + $"Schema.For<{typeof(TDoc).Name}>().VectorIndex(x => x.{nameof(IVectorized<TId>.Embedding)}, "
                + $"{_provider.Dimensions})"
                + (indexes.Count == 0
                    ? "."
                    : $" — it declares one on '{string.Join(", ", indexes.Select(x => x.MemberName))}' instead."));
        }

        if (index.Dimensions != _provider.Dimensions)
        {
            throw new InvalidOperationException(
                $"'{typeof(TDoc).Name}.{nameof(IVectorized<TId>.Embedding)}' was declared with "
                + $"{index.Dimensions} dimensions and {_provider.GetType().Name} produces "
                + $"{_provider.Dimensions}. The rows would be written and then refused by every "
                + "search, with a message about the caller's query vector rather than about this "
                + "projection.");
        }
    }

    private static string Sha256(string content)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
