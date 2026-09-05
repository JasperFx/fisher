using System.Collections.Concurrent;
using System.Reflection;
using Fisher.Events;
using JasperFx.Events;

namespace Fisher.Tests.Events;

/// <summary>
///     The two per-event hot-path caches on <see cref="EventGraph" /> (fisher#156): the compiled
///     <c>Event&lt;T&gt;</c> wrapper on <see cref="FisherEventType.Wrap" />, and the memoized
///     <c>Type.GetType</c> behind <c>ResolveEventType</c>. Both run once per event row hydrated, so
///     these tests pin the behaviour the caches must not change and the one property a behavioural
///     test cannot see — that a resolution miss is cached rather than re-probed per row.
/// </summary>
public class event_wrapping_and_type_resolution
{
    private record LineWetted(Guid RodId, string Fly);

    private static EventGraph NewGraph() => new StoreOptions().EventGraph;

    [Fact]
    public void wrap_produces_a_typed_envelope_carrying_the_mappings_metadata()
    {
        var graph = NewGraph();
        var mapping = graph.EventMappingFor(typeof(LineWetted));
        var data = new LineWetted(Guid.NewGuid(), "elk hair caddis");

        var @event = mapping.Wrap(data);

        var typed = @event.ShouldBeOfType<Event<LineWetted>>();
        typed.Data.ShouldBeSameAs(data);
        @event.EventTypeName.ShouldBe(mapping.EventTypeName);
        @event.DotNetTypeName.ShouldBe(mapping.DotNetTypeName);
    }

    /// <remarks>
    ///     Two wraps of one mapping must hand back two envelopes — the compiled wrapper is a cached
    ///     <em>constructor</em>, and caching the envelope instead would let one event's stamped
    ///     metadata (sequence, version, timestamp) bleed into the next row's.
    /// </remarks>
    [Fact]
    public void a_second_wrap_is_a_fresh_envelope()
    {
        var mapping = NewGraph().EventMappingFor(typeof(LineWetted));
        var data = new LineWetted(Guid.NewGuid(), "woolly bugger");

        var first = mapping.Wrap(data);
        var second = mapping.Wrap(data);

        second.ShouldNotBeSameAs(first);
        second.Data.ShouldBeSameAs(data);
    }

    [Fact]
    public void a_known_dotnet_type_name_resolves_and_is_cached()
    {
        var graph = NewGraph();
        var name = graph.EventMappingFor(typeof(LineWetted)).DotNetTypeName;

        graph.ResolveEventType(name).ShouldBe(typeof(LineWetted));

        var cache = CacheOf(graph);
        cache.TryGetValue(name, out var cached).ShouldBeTrue();
        cached.ShouldBe(typeof(LineWetted));
    }

    /// <remarks>
    ///     The half of fisher#156 a behavioural assertion cannot see. Fisher deliberately answers
    ///     null for a type this process does not know, so a stream holding foreign event types asks
    ///     this question once per row — without the cached miss every one of those rows re-runs
    ///     <c>Type.GetType</c>'s name parse and assembly probe. The stored null entry is the pin.
    /// </remarks>
    [Fact]
    public void an_unresolvable_name_is_null_and_the_miss_is_cached()
    {
        var graph = NewGraph();
        const string foreign = "Some.Other.Deployment.QuestStarted, Some.Other.Deployment";

        graph.ResolveEventType(foreign).ShouldBeNull();
        graph.ResolveEventType(foreign).ShouldBeNull();

        var cache = CacheOf(graph);
        cache.TryGetValue(foreign, out var cached).ShouldBeTrue();
        cached.ShouldBeNull();
    }

    [Fact]
    public void a_null_or_empty_name_is_null_and_never_reaches_the_cache()
    {
        var graph = NewGraph();

        graph.ResolveEventType(null).ShouldBeNull();
        graph.ResolveEventType("").ShouldBeNull();

        CacheOf(graph).ShouldBeEmpty();
    }

    [Fact]
    public void build_event_wraps_raw_data_through_the_mapping()
    {
        var graph = NewGraph();
        var data = new LineWetted(Guid.NewGuid(), "royal wulff");

        var @event = graph.BuildEvent(data);

        @event.ShouldBeOfType<Event<LineWetted>>().Data.ShouldBeSameAs(data);
        @event.EventTypeName.ShouldBe(graph.EventMappingFor(typeof(LineWetted)).EventTypeName);
    }

    private static ConcurrentDictionary<string, Type?> CacheOf(EventGraph graph)
        => (ConcurrentDictionary<string, Type?>)typeof(EventGraph)
            .GetField("_eventTypeByDotNetName", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(graph)!;
}
