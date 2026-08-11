using System.Text;
using System.Text.Json;
using Fisher.Linq;
using JasperFx;
using Microsoft.AspNetCore.Http;

namespace Fisher.AspNetCore.Tests;

/// <summary>
///     fisher#62 — the hardening Marten added to its streaming/ETag surface after fisher#49 was built
///     to marten#5015 parity.
/// </summary>
/// <remarks>
///     <para>
///         Five findings, ported as a matrix rather than as five separate ports, because what they have
///         in common is the point: each is a case where the result type returns something plausible.
///         A missing ETag looks like a cache miss, a body that is not the document is still a 200, and
///         a malformed cursor is a client error reported as a server one.
///     </para>
///     <para>
///         Two of the five reproduced here (marten#5120 and marten#5029) and are fixed. The other three
///         did not, for reasons that are Fisher's storage rather than luck, and are pinned so they stay
///         that way — see each test.
///     </para>
/// </remarks>
public class streaming_hardening : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("aspnet-hardening");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Chart>().UseOptimisticConcurrency();
            options.Schema.For<Log>().UseNumericRevisions();
            options.Schema.For<Buoy>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Fisher's serializer writes camelCase, so a body read back has to be told.</summary>
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static (DefaultHttpContext Context, MemoryStream Body) NewContext()
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;

        return (context, body);
    }

    /// <summary>
    ///     marten#5120 — a numeric-revisioned document emits an ETag from its revision.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Reproduced.</b> The ETag path read <c>guid_version</c> and threw for a type without
    ///         one, naming <c>UseOptimisticConcurrency()</c> in the message — advice that is wrong for a
    ///         revisioned type, since the two are alternatives and a revisioned document has a perfectly
    ///         good version to report. So the whole numeric-revision half of the store could not be
    ///         served with an ETag at all.
    ///     </para>
    ///     <para>
    ///         Fisher's two flavors are two physical columns where Marten's share <c>mt_version</c>, so
    ///         this needed no fail-fast guard against both being on: <c>AssertConcurrencyIsCoherent</c>
    ///         already refuses that pair at configuration time.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_revisioned_document_emits_an_etag_from_its_revision()
    {
        var log = new Log { Id = Guid.NewGuid(), Text = "first" };

        await using (var session = _store.LightweightSession())
        {
            session.Store(log);
            await session.SaveChangesAsync(Token);
        }

        var (context, body) = NewContext();

        await using (var session = _store.QuerySession())
        {
            await new StreamOne<Log>(session.Query<Log>().Where(x => x.Id == log.Id)).ExecuteAsync(context);
        }

        context.Response.StatusCode.ShouldBe(200);
        context.Response.Headers.ETag.ToString().ShouldBe("\"1\"");
        JsonSerializer.Deserialize<Log>(body.ToArray(), Web)!.Text.ShouldBe("first");
    }

    /// <summary>
    ///     The revision ETag moves when the document does, which is the whole point of a validator.
    /// </summary>
    [Fact]
    public async Task a_revision_etag_changes_when_the_document_changes()
    {
        var log = new Log { Id = Guid.NewGuid(), Text = "first" };

        await using (var session = _store.LightweightSession())
        {
            session.Store(log);
            await session.SaveChangesAsync(Token);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Log { Id = log.Id, Text = "second" });
            await session.SaveChangesAsync(Token);
        }

        var (context, _) = NewContext();

        await using (var session = _store.QuerySession())
        {
            await new StreamOne<Log>(session.Query<Log>().Where(x => x.Id == log.Id)).ExecuteAsync(context);
        }

        context.Response.Headers.ETag.ToString().ShouldBe("\"2\"");
    }

    /// <summary>
    ///     A matching <c>If-None-Match</c> against a revision ETag is a 304, exactly as for a Guid one.
    /// </summary>
    [Fact]
    public async Task a_revisioned_document_honours_if_none_match()
    {
        var log = new Log { Id = Guid.NewGuid(), Text = "first" };

        await using (var session = _store.LightweightSession())
        {
            session.Store(log);
            await session.SaveChangesAsync(Token);
        }

        var (context, body) = NewContext();
        context.Request.Headers["If-None-Match"] = "\"1\"";

        await using (var session = _store.QuerySession())
        {
            await new StreamOne<Log>(session.Query<Log>().Where(x => x.Id == log.Id)).ExecuteAsync(context);
        }

        context.Response.StatusCode.ShouldBe(304);
        context.Response.Headers.ETag.ToString().ShouldBe("\"1\"");
        body.Length.ShouldBe(0);
    }

    /// <summary>
    ///     marten#5157 — a 304 writes no body at all.
    /// </summary>
    /// <remarks>
    ///     Already true here, and pinned rather than assumed: the 304 branch returns before the write
    ///     and sets <c>Content-Length: 0</c>. Note what Fisher does <em>not</em> claim — the row is
    ///     still read from SQLite, because the JSON and the version come back in one query so that a
    ///     200 needs no second one. What is saved is the copy into the response, which is the part that
    ///     scales with the document.
    /// </remarks>
    [Fact]
    public async Task a_304_writes_nothing_and_reports_zero_length()
    {
        var chart = new Chart { Id = Guid.NewGuid(), Name = "Solent" };

        await using (var session = _store.LightweightSession())
        {
            session.Store(chart);
            await session.SaveChangesAsync(Token);
        }

        string etag;

        var (first, _) = NewContext();

        await using (var session = _store.QuerySession())
        {
            await new StreamOne<Chart>(session.Query<Chart>().Where(x => x.Id == chart.Id)).ExecuteAsync(first);
            etag = first.Response.Headers.ETag.ToString();
        }

        var (context, body) = NewContext();
        context.Request.Headers["If-None-Match"] = etag;

        await using (var query = _store.QuerySession())
        {
            await new StreamOne<Chart>(query.Query<Chart>().Where(x => x.Id == chart.Id)).ExecuteAsync(context);
        }

        context.Response.StatusCode.ShouldBe(304);
        body.Length.ShouldBe(0);
        context.Response.ContentLength.ShouldBe(0);
    }

    /// <summary>
    ///     A 404 emits no ETag — there is no representation to validate against.
    /// </summary>
    [Fact]
    public async Task a_404_emits_no_etag()
    {
        var (context, body) = NewContext();

        await using (var session = _store.QuerySession())
        {
            await new StreamOne<Chart>(session.Query<Chart>().Where(x => x.Id == Guid.NewGuid()))
                .ExecuteAsync(context);
        }

        context.Response.StatusCode.ShouldBe(404);
        context.Response.Headers.ETag.ToString().ShouldBeEmpty();
        body.Length.ShouldBe(0);
    }

    /// <summary>
    ///     marten#5158 — a <c>Select</c> projection does not silently lose the payload column.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Did not reproduce, structurally.</b> Marten's failure was positional: its version
    ///         select clause rebuilt the select list and dropped the alias the inner clause would have
    ///         emitted, so the reader's <c>GetOrdinal("data")</c> found a column named after a
    ///         <c>jsonb_build_object</c> expression. Fisher's JSON reads name their columns outright
    ///         (<c>data, guid_version</c>) and refuse a projected query by name before any of that,
    ///         because a JSON read returns stored documents and a projection is not one.
    ///     </para>
    ///     <para>
    ///         Pinned as a refusal with a message that says which operator, rather than as an
    ///         exception about a missing column or an unmapped primitive type — Marten's second failure
    ///         mode.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_json_read_after_a_select_is_refused_by_name()
    {
        await using var session = _store.QuerySession();

        var ex = await Should.ThrowAsync<Exception>(async () =>
            await session.Query<Chart>().Select(x => x.Name).ToJsonArrayAsync(Token));

        ex.Message.ShouldContain("Select");
    }

    /// <summary>
    ///     marten#5166 — the body is the document, not its id, through a tracking session.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Did not reproduce, and for a reason worth stating.</b> Marten aliased the payload by
    ///         <em>position</em>, on the strength of a comment claiming the select list starts with
    ///         <c>data</c> — which is only true for a query-only session. Through an identity-tracking
    ///         one the id column is selected first, so the alias landed on the id and the endpoint
    ///         returned a 200 whose body was a bare Guid string.
    ///     </para>
    ///     <para>
    ///         Fisher's JSON read replaces the select list with named columns rather than aliasing
    ///         whatever the storage selected, so the session's tracking mode cannot move the payload.
    ///         Pinned across all three modes, since it is exactly the kind of coupling that gets
    ///         reintroduced by a change that looks like a tidy-up.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(DocumentTracking.None)]
    [InlineData(DocumentTracking.IdentityOnly)]
    [InlineData(DocumentTracking.DirtyTracking)]
    public async Task the_body_is_the_document_through_any_tracking_mode(DocumentTracking tracking)
    {
        var chart = new Chart { Id = Guid.NewGuid(), Name = "Solent" };

        await using (var session = _store.LightweightSession())
        {
            session.Store(chart);
            await session.SaveChangesAsync(Token);
        }

        var (context, body) = NewContext();

        await using (var session = _store.OpenSession(new SessionOptions { Tracking = tracking }))
        {
            await new StreamOne<Chart>(session.Query<Chart>().Where(x => x.Id == chart.Id)).ExecuteAsync(context);
        }

        context.Response.StatusCode.ShouldBe(200);

        // The failure this guards against is a 200 whose body is the id — which parses as JSON, and as
        // a Guid, and not as the document.
        JsonSerializer.Deserialize<Chart>(body.ToArray(), Web)!.Name.ShouldBe("Solent");
    }

    /// <summary>
    ///     marten#5029 — a tampered cursor is a client error, not a server one.
    /// </summary>
    /// <remarks>
    ///     <b>Reproduced.</b> The decode caught a malformed base64 or JSON payload and turned it into an
    ///     <see cref="ArgumentException" />, but the per-key <em>bind</em> was uncaught: a cursor whose
    ///     value is well-formed JSON of the wrong shape for its ordering key reached
    ///     <c>JsonElement.GetInt64()</c> and came out as an <see cref="InvalidOperationException" /> —
    ///     which an endpoint maps to a 500 for what is entirely the client's doing.
    /// </remarks>
    [Fact]
    public async Task a_cursor_whose_value_does_not_bind_is_an_argument_exception()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new Buoy { Id = Guid.NewGuid(), Depth = 3 });
            await session.SaveChangesAsync(Token);
        }

        // Well-formed cursor, well-formed JSON, wrong type for the key: Depth is an int.
        var tampered = "v1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("[\"not-a-number\",\"" + Guid.NewGuid() + "\"]"));

        await using var query = _store.QuerySession();

        await Should.ThrowAsync<ArgumentException>(async () =>
            await query.Query<Buoy>().OrderBy(x => x.Depth).ThenBy(x => x.Id)
                .ToCursorPageAsync(10, tampered, Token));
    }

    /// <summary>
    ///     The same, on the JSON cursor page the ASP.NET Core results use.
    /// </summary>
    [Fact]
    public async Task a_tampered_cursor_on_the_json_page_is_an_argument_exception()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new Buoy { Id = Guid.NewGuid(), Depth = 3 });
            await session.SaveChangesAsync(Token);
        }

        var tampered = "v1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("[{\"nested\":1},\"" + Guid.NewGuid() + "\"]"));

        await using var query = _store.QuerySession();

        await Should.ThrowAsync<ArgumentException>(async () =>
            await query.Query<Buoy>().OrderBy(x => x.Depth).ThenBy(x => x.Id)
                .ToJsonCursorPageAsync(10, tampered, Token));
    }

    public class Chart
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class Log
    {
        public Guid Id { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    public class Buoy
    {
        public Guid Id { get; set; }
        public int Depth { get; set; }
    }
}
