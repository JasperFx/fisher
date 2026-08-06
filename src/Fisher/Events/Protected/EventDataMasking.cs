using System.Linq.Expressions;
using JasperFx.Events;
using JasperFx.Events.Protected;

namespace Fisher.Events.Protected;

/// <summary>
///     Fisher's execution of <see cref="IEventDataMasking" /> — the GDPR-style erasure surface, reached
///     through <c>DocumentStore.Advanced.ApplyEventDataMasking(...)</c>.
/// </summary>
/// <remarks>
///     <para>
///         JasperFx 2.41.0 lifted the interface out of Marten and Polecat because both declared it
///         member-for-member identically; the *request* shape is database-agnostic and executing it is
///         not. This is the executing half, and it is deliberately close to Polecat's so masking code
///         ports between the stores unchanged.
///     </para>
///     <para>
///         Every source is resolved and every match rewritten in <b>one session</b>, so the whole batch
///         commits or none of it does. That matters more here than for most operations: a partial
///         erasure is a compliance answer that is neither "done" nor "not done", with no record of
///         which events were reached.
///     </para>
///     <para>
///         <b>What this does not do is reach a projection.</b> Masking rewrites <c>fi_events.data</c>
///         below the daemon's high-water mark, and the mark does not move, so a shard that has already
///         folded the unmasked body keeps whatever it derived from it. Any document, snapshot or flat
///         table holding the protected information still holds it, and a rebuild is what clears it.
///         Marten is the same. This is why masking is described as a data-at-rest operation — see
///         <see cref="OverwriteEventOperation" />, which carries the same caveat for the same reason.
///     </para>
/// </remarks>
internal sealed class EventDataMasking : IEventDataMasking
{
    private readonly DocumentStore _store;
    private readonly List<Func<IDocumentSession, CancellationToken, Task<IReadOnlyList<IEvent>>>> _sources = new();
    private readonly Dictionary<string, object> _headers = new();
    private string? _tenantId;

    internal EventDataMasking(DocumentStore store)
    {
        _store = store;
    }

    public IEventDataMasking ForTenant(string tenantId)
    {
        _tenantId = tenantId;
        return this;
    }

    public IEventDataMasking IncludeStream(Guid streamId)
    {
        _sources.Add((s, t) => s.Events.FetchStreamAsync(streamId, token: t));
        return this;
    }

    public IEventDataMasking IncludeStream(string streamKey)
    {
        _sources.Add((s, t) => s.Events.FetchStreamAsync(streamKey, token: t));
        return this;
    }

    public IEventDataMasking IncludeStream(Guid streamId, Func<IEvent, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        _sources.Add(async (s, t) =>
        {
            var events = await s.Events.FetchStreamAsync(streamId, token: t).ConfigureAwait(false);
            return events.Where(filter).ToList();
        });

        return this;
    }

    public IEventDataMasking IncludeStream(string streamKey, Func<IEvent, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        _sources.Add(async (s, t) =>
        {
            var events = await s.Events.FetchStreamAsync(streamKey, token: t).ConfigureAwait(false);
            return events.Where(filter).ToList();
        });

        return this;
    }

    /// <summary>
    ///     Every event matching a predicate over <see cref="IEvent" />'s own members, across streams.
    /// </summary>
    /// <remarks>
    ///     Translated to SQL, unlike the two <c>IncludeStream</c> filter overloads, which are ordinary
    ///     in-memory <c>Func</c>s applied to an already-fetched stream. That asymmetry is the
    ///     interface's, not Fisher's: the parameter types say so.
    /// </remarks>
    public IEventDataMasking IncludeEvents(Expression<Func<IEvent, bool>> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        _sources.Add((s, t) => s.Events.QueryEventsAsync(filter, t));
        return this;
    }

    public IEventDataMasking AddHeader(string key, object value)
    {
        ArgumentNullException.ThrowIfNull(key);

        _headers[key] = value;
        return this;
    }

    /// <summary>
    ///     Resolve every source, apply the registered rules, and commit the rewrites.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An event only reaches <c>OverwriteEvent</c> when a rule actually matched it. A source
    ///         that selects events no rule covers therefore writes nothing at all, rather than
    ///         rewriting them to what they already said — and the headers follow the same rule, so
    ///         <see cref="AddHeader" /> marks the events that were masked rather than the events that
    ///         were looked at.
    ///     </para>
    ///     <para>
    ///         The same event arriving from two sources is deduplicated by sequence. Overwriting it
    ///         twice would be harmless — the second write carries the same already-masked body — but it
    ///         would double the statements in a batch that can span a whole stream, and a rule that is
    ///         not idempotent would apply twice.
    ///     </para>
    /// </remarks>
    internal async Task ApplyAsync(CancellationToken token = default)
    {
        if (_sources.Count == 0)
        {
            throw new InvalidOperationException(
                "Specify at least one stream or event filter before applying a masking batch. "
                + "Masking every event in the store is not something this API can be asked for by accident.");
        }

        await using var session = BuildSession();

        var seen = new HashSet<long>();

        foreach (var source in _sources)
        {
            var events = await source(session, token).ConfigureAwait(false);

            foreach (var @event in events)
            {
                if (!seen.Add(@event.Sequence))
                {
                    continue;
                }

                if (!_store.EventGraph.TryMask(@event))
                {
                    continue;
                }

                foreach (var pair in _headers)
                {
                    @event.Headers ??= new Dictionary<string, object>();
                    @event.Headers[pair.Key] = pair.Value;
                }

                session.Events.OverwriteEvent(@event);
            }
        }

        await session.SaveChangesAsync(token).ConfigureAwait(false);
    }

    private IDocumentSession BuildSession()
        => string.IsNullOrEmpty(_tenantId)
            ? _store.LightweightSession()
            : _store.LightweightSession(_tenantId);
}
