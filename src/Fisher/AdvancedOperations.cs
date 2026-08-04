using Weasel.Core.Sequences;

namespace Fisher;

/// <summary>
///     The store's escape hatch: cleaning, resetting, and the Hi-Lo knobs — the things an application
///     reaches for outside the session API. Mirrors Marten's and Polecat's <c>AdvancedOperations</c>.
/// </summary>
public class AdvancedOperations
{
    private readonly DocumentStore _store;
    private IDocumentCleaner? _cleaner;

    internal AdvancedOperations(DocumentStore store)
    {
        _store = store;
    }

    /// <summary>
    ///     The Hi-Lo settings applied to any document type with a numeric identity and no override of
    ///     its own.
    /// </summary>
    public HiloSettings HiloSequenceDefaults => _store.Options.HiloSequenceDefaults;

    /// <inheritdoc cref="IDocumentCleaner" />
    public IDocumentCleaner Clean => _cleaner ??= new Internal.FisherDocumentCleaner(_store);

    /// <summary>
    ///     Delete every document and every event belonging to this store, keeping the schema.
    /// </summary>
    public async Task ResetAllDataAsync(CancellationToken token = default)
    {
        await Clean.DeleteAllDocumentsAsync(token).ConfigureAwait(false);
        await Clean.DeleteAllEventDataAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Advance a document type's Hi-Lo sequence so that every subsequently assigned id is greater
    ///     than <paramref name="floor" />.
    /// </summary>
    /// <remarks>
    ///     The floor rounds up to a whole allocation, so the next id is the start of the first page
    ///     past it rather than <paramref name="floor" /> + 1. That matches Marten and Polecat, and is
    ///     the price of the client-side batching Hi-Lo exists for.
    /// </remarks>
    public Task ResetHiloSequenceFloorAsync<T>(long floor) where T : notnull
        => _store.Database.SequenceFor(typeof(T)).SetFloor(floor);
}
