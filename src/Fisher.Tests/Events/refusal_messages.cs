using Fisher.Exceptions;
using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Events;

/// <summary>
///     A refusal names the way forward, not only what failed (fisher#399).
/// </summary>
/// <remarks>
///     The two stream exceptions take JasperFx's canonical wording (jasperfx#872 / marten#5475), and
///     every generic LINQ refusal names an escape hatch (marten#5481). The stream remedies make claims
///     about Fisher's behaviour, so the claims are pinned too, not only the text.
/// </remarks>
public class refusal_messages : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("refusal-messages");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task starting_an_existing_stream_says_to_append_or_fetch_for_writing()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(id, new RefusalNoted("first"));
            await session.SaveChangesAsync(Token);
        }

        await using var again = _store.LightweightSession();
        again.Events.StartStream(id, new RefusalNoted("second"));

        var ex = await Should.ThrowAsync<ExistingStreamIdCollisionException>(() => again.SaveChangesAsync(Token));
        ex.Message.ShouldContain("StartStream requires a new id");
        ex.Message.ShouldContain("FetchForWriting");
    }

    [Fact]
    public async Task appending_optimistically_to_a_missing_stream_says_to_start_it_first()
    {
        await using var session = _store.LightweightSession();

        var ex = await Should.ThrowAsync<NonExistentStreamException>(
            () => session.Events.AppendOptimistic(Guid.NewGuid(), Token, new RefusalNoted("orphan")));
        ex.Message.ShouldContain("call StartStream first");
    }

    /// <remarks>
    ///     The canonical remedy says a plain Append starts a missing stream. That is a claim about Fisher's
    ///     behaviour, so it is pinned here rather than trusted.
    /// </remarks>
    [Fact]
    public async Task a_plain_append_starts_a_missing_stream_as_the_remedy_says()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.Append(id, new RefusalNoted("first"));
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.Events.FetchStreamStateAsync(id, Token)).ShouldNotBeNull();
    }

    /// <remarks>
    ///     The Fisher-specific case the canonical message cannot name: StartStream over an ARCHIVED id is
    ///     a collision, not an archived-stream error, because the id is still in use.
    /// </remarks>
    [Fact]
    public async Task starting_a_stream_over_an_archived_id_is_a_collision()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(id, new RefusalNoted("first"));
            await session.SaveChangesAsync(Token);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Events.ArchiveStream(id);
            await session.SaveChangesAsync(Token);
        }

        await using var again = _store.LightweightSession();
        again.Events.StartStream(id, new RefusalNoted("second"));
        await Should.ThrowAsync<ExistingStreamIdCollisionException>(() => again.SaveChangesAsync(Token));
    }

    // ---- LINQ ----

    [Fact]
    public async Task an_unsupported_method_in_a_where_clause_names_the_escape_hatches()
    {
        await using var session = _store.QuerySession();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(() => session.Query<RefusalDoc>()
            .Where(x => x.Name.IsNormalized()).ToListAsync(Token));

        ex.Message.ShouldContain("Unsupported method call");
        ex.Message.ShouldContain("MatchesSql");
        ex.Message.ShouldContain("AdvancedSql");
    }

    [Fact]
    public async Task an_unsupported_binary_operator_names_the_escape_hatches()
    {
        await using var session = _store.QuerySession();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(() => session.Query<RefusalDoc>()
            .Where(x => x.Flag ^ true).ToListAsync(Token));

        ex.Message.ShouldContain("Unsupported binary operator");
        ex.Message.ShouldContain("MatchesSql");
    }

    [Fact]
    public async Task an_untranslatable_comparison_names_the_escape_hatches()
    {
        await using var session = _store.QuerySession();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(() => session.Query<RefusalDoc>()
            .Where(x => (x.Count ^ 1) == 0).ToListAsync(Token));

        ex.Message.ShouldContain("Cannot translate the comparison");
        ex.Message.ShouldContain("MatchesSql");
    }

    /// <remarks>
    ///     The catch-all used to list Where, Select, Distinct, DistinctBy, the four orderings, Take and
    ///     Skip, and to say "yet". GroupBy, Join and the relevance orderings were all supported and missing
    ///     from the list a refused caller reads.
    /// </remarks>
    [Fact]
    public async Task an_unsupported_operator_lists_what_is_supported()
    {
        await using var session = _store.QuerySession();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(() => session.Query<RefusalDoc>()
            .Reverse().ToListAsync(Token));

        ex.Message.ShouldContain("GroupBy");
        ex.Message.ShouldContain("Join");
        ex.Message.ShouldContain("OrderByRelevance");
        ex.Message.ShouldContain("MatchesSql");
        ex.Message.ShouldNotContain("yet");
    }
}

public record RefusalNoted(string Text);

public class RefusalDoc
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public bool Flag { get; set; }
}
