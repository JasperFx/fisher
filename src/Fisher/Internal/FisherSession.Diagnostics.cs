using System.Globalization;

namespace Fisher.Internal;

/// <summary>
///     The two session members <c>IDocumentStoreDiagnosticsWriter</c> needs and no application does
///     (fisher#364 / jasperfx#870 §6).
/// </summary>
/// <remarks>
///     The writer runs an enlisted session inside a transaction it opened itself, having already read
///     the row and checked the caller's expected version under that transaction's write lock. So what it
///     needs from the session is a write that trusts that check rather than making its own — which is
///     the one thing an ordinary <c>Store</c> cannot be asked for.
/// </remarks>
internal partial class FisherSession
{
    /// <summary>
    ///     Queue an upsert whose concurrency guard expects exactly what the writer just read.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A Guid-versioned row is seeded with the version read under the lock.</b> Fisher's guard
    ///         is fed from what the session read (fisher#245), and a writer that opened this session a
    ///         moment ago has read nothing — so without the seed an unconditional console save of an
    ///         existing optimistic document fails its guard every time, and a guarded one fails too.
    ///     </para>
    ///     <para>
    ///         <b>A numeric revision is written as zero, meaning auto.</b> Marten's rule, which Fisher
    ///         follows, requires an explicit revision to be strictly greater than the stored one, and the
    ///         JSON a console hands back carries the revision it read — so honouring it would refuse
    ///         every edit. The staleness question has already been answered against the column.
    ///     </para>
    /// </remarks>
    internal void StoreForDiagnostics<T>(T document, Guid? currentVersion) where T : notnull
    {
        var storage = StorageFor<T>();

        if (currentVersion is { } version && version != Guid.Empty)
        {
            storage.Store(this, document, version);
        }
        else
        {
            storage.Store(this, document);
        }

        QueueOperation(CaptureExpectedRevision(storage.Upsert(document, this, TenantId), document, revision: 0));
    }

    /// <summary>
    ///     The document's identity in the invariant text form the id column holds, so the writer can
    ///     compare it against the id the console named.
    /// </summary>
    /// <remarks>
    ///     Through the storage's own identity strategy rather than a member read, because that is what
    ///     unwraps a strong-typed id and renders a Guid lowercase — the two ways a hand-rolled
    ///     comparison would disagree with the column.
    /// </remarks>
    internal string IdentityTextForDiagnostics<T>(T document) where T : notnull
    {
        var storage = StorageFor<T>();
        return InvariantText(storage.RawIdentityValue(storage.IdentityFor(document)));
    }

    internal static string InvariantText(object? value) => value switch
    {
        null => string.Empty,
        Guid guid => guid.ToString("D"),
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };
}
