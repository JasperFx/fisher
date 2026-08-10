using System.Text;
using System.Text.Json;
using Fisher;
using Fisher.AspNetCore;
using Fisher.Linq;
using JasperFx;
using Microsoft.AspNetCore.Http;

namespace Fisher.AspNetCore.Tests;

/// <summary>
///     fisher#49 — the streaming <c>IResult</c> types.
/// </summary>
/// <remarks>
///     <para>
///         <b>Tested against a <see cref="DefaultHttpContext" /> rather than through a test host.</b>
///         These are <c>IResult</c> implementations whose entire job is what they write to a response,
///         so a context with a <see cref="MemoryStream" /> body is the thing under test; a web host
///         would add a request pipeline that none of the assertions are about.
///     </para>
///     <para>
///         The claim that matters and that neither sibling can make: <b>what the endpoint returns is
///         byte-for-byte what was stored</b>. Asserted against the serializer's own output rather than
///         a hand-written literal, so it stays true if the serializer's settings change.
///     </para>
/// </remarks>
public class streaming_results : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("aspnet-streaming");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Chart>().UseOptimisticConcurrency();
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

    private static (DefaultHttpContext Context, MemoryStream Body) NewContext()
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;

        return (context, body);
    }

    private static string Read(MemoryStream body) => Encoding.UTF8.GetString(body.ToArray());

    // ---- documents ----

    [Fact]
    public async Task stream_one_writes_the_stored_bytes()
    {
        var chart = new Chart { Id = Guid.NewGuid(), Name = "Admiralty 1", Scale = 25_000 };

        await using (var session = _store.LightweightSession())
        {
            session.Store(chart);
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        var (context, body) = NewContext();

        await query.Query<Chart>().Where(x => x.Id == chart.Id).StreamOne().ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.ContentType.ShouldBe("application/json");

        // Byte-exact against what the serializer wrote, not against a literal — the guarantee neither
        // sibling can make, because jsonb normalises and nvarchar needs an encoding decision.
        Read(body).ShouldBe(_store.Options.Serializer.ToJson(chart));
    }

    [Fact]
    public async Task stream_one_is_a_404_when_nothing_matches()
    {
        await using var query = _store.LightweightSession();
        var (context, body) = NewContext();

        await query.Query<Chart>().Where(x => x.Id == Guid.NewGuid()).StreamOne().ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        Read(body).ShouldBeEmpty();
    }

    /// <remarks>
    ///     A type with no <c>guid_version</c> has no ETag to emit, so the read that asks for one
    ///     refuses by name. <c>EmitETag = false</c> is the way through, and it still streams the stored
    ///     bytes.
    /// </remarks>
    [Fact]
    public async Task a_type_without_a_version_streams_with_the_etag_turned_off()
    {
        var buoy = new Buoy { Id = Guid.NewGuid(), Name = "North Cardinal" };

        await using (var session = _store.LightweightSession())
        {
            session.Store(buoy);
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();

        var (failing, _) = NewContext();
        await Should.ThrowAsync<InvalidOperationException>(async ()
            => await new StreamOne<Buoy>(query.Query<Buoy>().Where(x => x.Id == buoy.Id))
                .ExecuteAsync(failing));

        var (context, body) = NewContext();
        await new StreamOne<Buoy>(query.Query<Buoy>().Where(x => x.Id == buoy.Id)) { EmitETag = false }
            .ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        Read(body).ShouldBe(_store.Options.Serializer.ToJson(buoy));
    }

    [Fact]
    public async Task stream_many_writes_a_json_array_of_the_stored_bytes()
    {
        var first = new Chart { Id = Guid.NewGuid(), Name = "A", Scale = 1 };
        var second = new Chart { Id = Guid.NewGuid(), Name = "B", Scale = 2 };

        await using (var session = _store.LightweightSession())
        {
            session.Store(first, second);
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        var (context, body) = NewContext();

        await query.Query<Chart>().OrderBy(x => x.Name).StreamMany().ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);

        // Concatenated, not re-rendered: each element is exactly the stored document.
        Read(body).ShouldBe(
            $"[{_store.Options.Serializer.ToJson(first)},{_store.Options.Serializer.ToJson(second)}]");
    }

    [Fact]
    public async Task stream_many_writes_an_empty_array_rather_than_a_404()
    {
        await using var query = _store.LightweightSession();
        var (context, body) = NewContext();

        await query.Query<Chart>().Where(x => x.Name == "nope").StreamMany().ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        Read(body).ShouldBe("[]");
    }

    /// <remarks>
    ///     The total rides a header rather than an envelope, so the body stays the stored bytes — and
    ///     it is a second statement rather than a window function, because a window function returns no
    ///     row at all for a page past the end, which is exactly when a pager most needs the total.
    /// </remarks>
    [Fact]
    public async Task stream_paged_reports_the_total_past_the_end()
    {
        await using (var session = _store.LightweightSession())
        {
            for (var i = 0; i < 5; i++)
            {
                session.Store(new Chart { Id = Guid.NewGuid(), Name = $"Chart {i}", Scale = i });
            }

            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();

        var (page, pageBody) = NewContext();
        await query.Query<Chart>().OrderBy(x => x.Name).StreamPaged(2, 2).ExecuteAsync(page);

        page.Response.Headers[StreamPaged<Chart>.TotalCountHeader].ToString().ShouldBe("5");
        JsonDocument.Parse(Read(pageBody)).RootElement.GetArrayLength().ShouldBe(2);

        var (beyond, beyondBody) = NewContext();
        await query.Query<Chart>().OrderBy(x => x.Name).StreamPaged(9, 2).ExecuteAsync(beyond);

        beyond.Response.Headers[StreamPaged<Chart>.TotalCountHeader].ToString().ShouldBe("5");
        Read(beyondBody).ShouldBe("[]");
    }

    // ---- ETags ----

    /// <remarks>
    ///     The round trip an ETag exists for: read once, send the tag back, get a <c>304</c> with an
    ///     empty body.
    /// </remarks>
    [Fact]
    public async Task an_unchanged_document_is_a_304()
    {
        var chart = new Chart { Id = Guid.NewGuid(), Name = "Admiralty 1", Scale = 25_000 };

        await using (var session = _store.LightweightSession())
        {
            session.Store(chart);
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();

        var (first, _) = NewContext();
        await query.Query<Chart>().Where(x => x.Id == chart.Id).StreamOne().ExecuteAsync(first);

        var etag = first.Response.Headers.ETag.ToString();
        etag.ShouldNotBeNullOrEmpty();

        var (second, secondBody) = NewContext();
        second.Request.Headers["If-None-Match"] = etag;

        await query.Query<Chart>().Where(x => x.Id == chart.Id).StreamOne().ExecuteAsync(second);

        second.Response.StatusCode.ShouldBe(StatusCodes.Status304NotModified);
        second.Response.ContentLength.ShouldBe(0);
        Read(secondBody).ShouldBeEmpty();
    }

    /// <remarks>
    ///     A stale tag has to produce the document, or a client that cached an old version never sees
    ///     the new one.
    /// </remarks>
    [Fact]
    public async Task a_stale_etag_gets_the_document()
    {
        var chart = new Chart { Id = Guid.NewGuid(), Name = "Admiralty 1", Scale = 25_000 };

        await using (var session = _store.LightweightSession())
        {
            session.Store(chart);
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        var (context, body) = NewContext();
        context.Request.Headers["If-None-Match"] = "\"00000000-0000-0000-0000-000000000001\"";

        await query.Query<Chart>().Where(x => x.Id == chart.Id).StreamOne().ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        Read(body).ShouldNotBeEmpty();
    }

    // ---- cursor paging ----

    /// <remarks>
    ///     The cursor is emitted in a header and in the envelope: a header lets a client follow the
    ///     page without parsing the body, the envelope keeps the response self-describing for one that
    ///     already has.
    /// </remarks>
    [Fact]
    public async Task cursor_paging_walks_the_whole_set_once()
    {
        await using (var session = _store.LightweightSession())
        {
            for (var i = 0; i < 5; i++)
            {
                session.Store(new Chart { Id = Guid.NewGuid(), Name = $"Chart {i}", Scale = i });
            }

            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();

        var seen = new List<string>();
        string? cursor = null;

        for (var page = 0; page < 5; page++)
        {
            var (context, body) = NewContext();

            await query.Query<Chart>().OrderBy(x => x.Name).ThenBy(x => x.Id)
                .StreamPagedByCursor(cursor, 2).ExecuteAsync(context);

            var envelope = JsonDocument.Parse(Read(body)).RootElement;

            foreach (var item in envelope.GetProperty("items").EnumerateArray())
            {
                seen.Add(item.GetProperty("name").GetString()!);
            }

            cursor = envelope.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
                ? null
                : envelope.GetProperty("nextCursor").GetString();

            // The header agrees with the envelope, or a client following one gets a different walk.
            var header = context.Response.Headers[StreamPagedByCursor<Chart>.ContinuationHeader].ToString();
            (header.Length == 0 ? null : header).ShouldBe(cursor);

            if (cursor is null)
            {
                break;
            }
        }

        seen.ShouldBe(["Chart 0", "Chart 1", "Chart 2", "Chart 3", "Chart 4"]);
    }
}

public class Chart
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Scale { get; set; }
}

public class Buoy
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
