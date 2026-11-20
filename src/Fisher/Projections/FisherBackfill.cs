using System.Diagnostics.CodeAnalysis;
using Fisher.Internal;
using JasperFx.Events;

namespace Fisher.Projections;

/// <summary>
///     jasperfx#1222 / fisher#643: a backfill declared with <see cref="SingleStreamProjection{TDoc,TId}.Backfill" />.
///     Each handler changes a document that already exists. Documents that are missing or deleted are skipped.
/// </summary>
/// <typeparam name="TDoc">The projection's document type.</typeparam>
public sealed class FisherBackfill<TDoc> : IFisherBackfill where TDoc : notnull
{
    private readonly List<(Type EventType, Action<TDoc, object> Apply)> _handlers = new();

    internal FisherBackfill(string name)
    {
        Name = name;
    }

    /// <summary>The backfill's name, the one <c>SubscribeFromPriorVersionMark</c> names.</summary>
    public string Name { get; }

    /// <summary>
    ///     Fill the new member from each <typeparamref name="TEvent" /> up to the backfill's ceiling.
    /// </summary>
    public FisherBackfill<TDoc> On<TEvent>(Action<TDoc, TEvent> apply) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(apply);
        _handlers.Add((typeof(TEvent), (doc, e) => apply(doc, (TEvent)e)));
        return this;
    }

    IReadOnlyList<Type> IFisherBackfill.EventTypes => _handlers.Select(x => x.EventType).Distinct().ToList();

    internal void Apply(TDoc document, object eventData)
    {
        foreach (var handler in _handlers)
        {
            if (handler.EventType.IsInstanceOfType(eventData))
            {
                handler.Apply(document, eventData);
            }
        }
    }
}

/// <summary>
///     jasperfx#1222: a projection that declares backfills.
/// </summary>
internal interface IFisherBackfillSource
{
    string Name { get; }
    uint Version { get; }
    IReadOnlyList<IFisherBackfill> Backfills { get; }
    Type DocumentType { get; }
    IFisherBackfillRunner RunnerFor(IFisherBackfill backfill);
}

internal interface IFisherBackfill
{
    string Name { get; }

    /// <summary>The event types the handlers were declared for, which may be interfaces or base types.</summary>
    IReadOnlyList<Type> EventTypes { get; }
}

/// <summary>
///     The half of a backfill page that needs the document's types: load the documents the events belong to, apply
///     the handlers and queue the writes on the page's session.
/// </summary>
internal interface IFisherBackfillRunner
{
    Type DocumentType { get; }

    /// <summary>Returns how many events were applied to a document that exists.</summary>
    Task<long> ApplyAsync(FisherSession session, IReadOnlyList<IEvent> events, CancellationToken token);
}

[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "IEvent.CreateAggregateIdentitySource resolves a strong-typed id's shape; TId is preserved by the projection's registration, as it is for the daemon.")]
internal sealed class FisherBackfillRunner<TDoc, TId> : IFisherBackfillRunner where TDoc : notnull where TId : notnull
{
    private static readonly Func<IEvent, TId> _identity = IEvent.CreateAggregateIdentitySource<TId>();

    private readonly FisherBackfill<TDoc> _backfill;

    public FisherBackfillRunner(FisherBackfill<TDoc> backfill)
    {
        _backfill = backfill;
    }

    public Type DocumentType => typeof(TDoc);

    public async Task<long> ApplyAsync(FisherSession session, IReadOnlyList<IEvent> events, CancellationToken token)
    {
        if (events.Count == 0) return 0;

        var storage = await ((IStorageOperations)session)
            .FetchProjectionStorageAsync<TDoc, TId>(session.TenantId, token).ConfigureAwait(false);

        // Read inside the page's transaction, so nothing else can write these documents between the load and
        // the write below
        var ids = events.Select(_identity).Distinct().ToArray();
        var documents = await storage.LoadManyAsync(ids, token).ConfigureAwait(false);

        long applied = 0;
        var changed = new Dictionary<TId, TDoc>();
        foreach (var e in events)
        {
            var id = _identity(e);
            if (!documents.TryGetValue(id, out var document)) continue;

            _backfill.Apply(document, e.Data);
            changed[id] = document;
            applied++;
        }

        foreach (var (id, document) in changed)
        {
            storage.Store(document, id, session.TenantId);
        }

        return applied;
    }
}
