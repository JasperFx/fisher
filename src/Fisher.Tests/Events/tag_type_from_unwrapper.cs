using Fisher.Tests.Schema;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Tags;

namespace Fisher.Tests.Events;

public record ProvinceId(Guid Value);

/// <summary>
///     <c>RegisterTagType&lt;TTag, TInner&gt;(Func&lt;TTag, TInner&gt;)</c>: the registration a trimmed or
///     Native AOT application uses, because the inferring overloads carry
///     <c>[RequiresUnreferencedCode]</c> / <c>[RequiresDynamicCode]</c> from
///     <c>TagTypeRegistration.Create&lt;TTag&gt;</c> (JasperFx 2.90.0). It wraps
///     <c>TagTypeRegistration.FromUnwrapper</c>, and has to land in the same table, with the same
///     suffix, as the inferring path would have.
/// </summary>
public class tag_type_from_unwrapper : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("tag_unwrapper");
    private DocumentStore _store = null!;
    private ITagTypeRegistration _registration = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            _registration = options.Events.RegisterTagType<ProvinceId, Guid>(id => id.Value);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public void the_registration_carries_the_explicit_inner_type_and_the_inferred_default_suffix()
    {
        _registration.TagType.ShouldBe(typeof(ProvinceId));
        _registration.SimpleType.ShouldBe(typeof(Guid));
        _registration.TableSuffix.ShouldBe("province_id");
    }

    [Fact]
    public void registering_the_same_tag_type_again_returns_the_first_registration()
    {
        _store.Options.EventGraph.RegisterTagType<ProvinceId, Guid>(id => id.Value, "other")
            .ShouldBeSameAs(_registration);
        _store.Options.EventGraph.TagTypes.Count(t => t.TagType == typeof(ProvinceId)).ShouldBe(1);
    }

    [Fact]
    public async Task a_tag_registered_from_an_unwrapper_round_trips()
    {
        var province = new ProvinceId(Guid.NewGuid());

        await using (var session = _store.LightweightSession())
        {
            var @event = session.Events.BuildEvent(new QuestStarted("Unwrapped"));
            @event.WithTag(province);
            session.Events.StartStream(Guid.NewGuid(), @event);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var events = await query.Events.QueryByTagsAsync(
            new EventTagQuery().Or<ProvinceId>(province), TestContext.Current.CancellationToken);

        events.Count.ShouldBe(1);
        events[0].Data.ShouldBeOfType<QuestStarted>().Name.ShouldBe("Unwrapped");
    }
}
