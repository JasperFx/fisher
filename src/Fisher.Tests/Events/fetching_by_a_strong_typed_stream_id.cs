using JasperFx;
using JasperFx.Events;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#397 — <c>FetchForWriting&lt;T, TId&gt;</c> and <c>FetchLatest&lt;T, TId&gt;</c> by a
///     strong-typed id wrapping the store's stream identity type.
/// </summary>
/// <remarks>
///     Both used to throw <see cref="NotImplementedException" /> saying strong-typed ids were "not
///     implemented yet", long after fisher#14 shipped them. A wrapper never reached the Guid or string
///     overload because nothing unwrapped it.
/// </remarks>
public class fetching_by_a_strong_typed_stream_id
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task a_guid_wrapper_fetches_for_writing_and_latest()
    {
        using var database = TemporaryDatabase.Create("strong-stream-id-guid");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        var id = Guid.NewGuid();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Kiln>(id, new KilnFired(900), new KilnFired(1100));
            await session.SaveChangesAsync(Token);
        }

        await using (var session = store.LightweightSession())
        {
            var stream = await session.Events.FetchForWriting<Kiln, KilnId>(new KilnId(id), Token);

            stream.Aggregate.ShouldNotBeNull();
            stream.Aggregate.Firings.ShouldBe(2);
            stream.CurrentVersion.ShouldBe(2);

            // The handle writes to the stream the wrapper named, not to a new one.
            stream.AppendOne(new KilnFired(1200));
            await session.SaveChangesAsync(Token);
        }

        await using (var session = store.LightweightSession())
        {
            var latest = await session.Events.FetchLatest<Kiln, KilnId>(new KilnId(id), Token);

            latest.ShouldNotBeNull();
            latest.Firings.ShouldBe(3);
        }
    }

    [Fact]
    public async Task a_string_wrapper_fetches_for_writing_and_latest()
    {
        using var database = TemporaryDatabase.Create("strong-stream-id-string");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.StreamIdentity = StreamIdentity.AsString;
        });

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Kettle>("kettle-1", new KilnFired(100));
            await session.SaveChangesAsync(Token);
        }

        await using var reader = store.LightweightSession();

        var stream = await reader.Events.FetchForWriting<Kettle, KettleKey>(new KettleKey("kettle-1"), Token);
        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Firings.ShouldBe(1);

        var latest = await reader.Events.FetchLatest<Kettle, KettleKey>(new KettleKey("kettle-1"), Token);
        latest.ShouldNotBeNull();
        latest.Firings.ShouldBe(1);
    }

    /// <remarks>
    ///     A wrapper around the OTHER identity type is still refused, by name. Converting a string to a
    ///     Guid, or the reverse, would address a stream the caller did not name.
    /// </remarks>
    [Fact]
    public async Task a_wrapper_around_the_wrong_inner_type_is_refused_by_name()
    {
        using var database = TemporaryDatabase.Create("strong-stream-id-wrong");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await using var session = store.LightweightSession();

        var forWriting = await Should.ThrowAsync<NotSupportedException>(
            () => session.Events.FetchForWriting<Kiln, KettleKey>(new KettleKey("nope"), Token));
        forWriting.Message.ShouldContain("KettleKey");
        forWriting.Message.ShouldContain("stream identity is Guid");
        forWriting.Message.ShouldNotContain("not implemented");

        await Should.ThrowAsync<NotSupportedException>(
            async () => await session.Events.FetchLatest<Kiln, KettleKey>(new KettleKey("nope"), Token));
    }
}

public readonly record struct KilnId(Guid Value);

public readonly record struct KettleKey(string Value);

public record KilnFired(int Temperature);

public class Kiln
{
    public KilnId Id { get; set; }
    public int Firings { get; set; }

    public void Apply(KilnFired fired) => Firings++;
}

public class Kettle
{
    public KettleKey Id { get; set; }
    public int Firings { get; set; }

    public void Apply(KilnFired fired) => Firings++;
}
