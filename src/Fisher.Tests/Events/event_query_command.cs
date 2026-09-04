using System.Text.Json;
using JasperFx;
using JasperFx.Events.CommandLine;
using Microsoft.Extensions.Hosting;

namespace Fisher.Tests.Events;

/// <summary>
///     End-to-end coverage of the <c>event-query</c> CLI command (jasperfx#737) against a real Fisher
///     store: <see cref="EventQueryCommand.Execute" /> driven with a real <see cref="EventQueryInput" />,
///     a host built the way the command builds one, stdout captured and parsed as the JSON an agent or
///     script would consume.
/// </summary>
/// <remarks>
///     <para>
///         Upstream owns input-mapping unit tests (<c>EventQueryInputTests</c>) and Fisher owns the
///         store-side behavior through <c>EventQueryCompliance</c> — but nothing between them executes
///         the command itself against a real store, and the command is where host building, store
///         discovery, the guard-rail catch, and the JSON rendering all meet. This class is that seam,
///         per store, as gh-148's follow-up asks.
///     </para>
///     <para>
///         Stdout capture swaps <see cref="Console.Out" /> for the duration of one
///         <see cref="EventQueryCommand.Execute" /> call. Other tests in this assembly can write to the
///         console concurrently, so the parse does not assume the captured text is exactly one JSON
///         document — it brace-counts from the report's opening line instead. See
///         <see cref="runAsync" />.
///     </para>
/// </remarks>
public class event_query_command : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("cli_event_query");
    private Guid _questId;

    /// <summary>
    ///     Seed through a store of this test's own, then dispose it: the command builds its OWN host
    ///     and store from <see cref="EventQueryInput.HostBuilder" />, which is the end-to-end point —
    ///     the events it reports are the ones another process's store left in the file.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = store.LightweightSession();
        _questId = Guid.NewGuid();

        // Sequences 1..3 on the quest stream, 4 on a second stream. The quest_started events are the
        // decoys that prove the member_joined filter filtered.
        session.Events.StartStream<Quest>(_questId,
            new QuestStarted("Find the ring"),
            new MemberJoined("Frodo"),
            new MemberJoined("Sam"));
        session.Events.StartStream<Quest>(Guid.NewGuid(), new QuestStarted("Guard the shire"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     A fresh builder per call, exactly as an application's <c>Program</c> hands one to
    ///     JasperFx's command line: the command disposes the host it builds, so a builder cannot be
    ///     reused across Execute calls.
    /// </summary>
    private IHostBuilder fisherHostBuilder()
        => new HostBuilder().ConfigureServices(services => services.AddFisher(_database.ConnectionString));

    /// <summary>
    ///     Execute the command with stdout captured, and hand back the parsed JSON report alongside
    ///     the command's success/failure return. Capture + parse live in <see cref="CliJsonCapture" />,
    ///     shared with the other CLI e2e classes.
    /// </summary>
    private Task<(bool Success, JsonDocument Report)> runAsync(EventQueryInput input)
    {
        input.HostBuilder = fisherHostBuilder();

        return CliJsonCapture.RunAsync(() => new EventQueryCommand().Execute(input));
    }

    [Fact]
    public async Task a_filtered_paged_query_answers_as_json_with_the_exact_events_and_total()
    {
        var (success, report) = await runAsync(new EventQueryInput
        {
            EventTypeFlag = "member_joined",
            PageSizeFlag = 1
        });

        success.ShouldBeTrue();

        var root = report.RootElement;

        // The filtered total across every page — 2 of the 4 seeded events — with page one holding
        // exactly the first match by store-global sequence.
        root.GetProperty("totalCount").GetInt32().ShouldBe(2);
        root.GetProperty("pageNumber").GetInt32().ShouldBe(1);
        root.GetProperty("pageSize").GetInt32().ShouldBe(1);
        root.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
        root.TryGetProperty("error", out _).ShouldBeFalse();

        var events = root.GetProperty("events");
        events.GetArrayLength().ShouldBe(1);

        var first = events[0];
        first.GetProperty("sequence").GetInt64().ShouldBe(2);
        first.GetProperty("streamId").GetString().ShouldBe(_questId.ToString());
        first.GetProperty("version").GetInt64().ShouldBe(2);
        first.GetProperty("eventType").GetString().ShouldBe("member_joined");
        first.GetProperty("data").GetProperty("Member").GetString().ShouldBe("Frodo");

        // Page two is the other match, and the paging metadata says the walk is over.
        var (secondSuccess, secondReport) = await runAsync(new EventQueryInput
        {
            EventTypeFlag = "member_joined",
            PageSizeFlag = 1,
            PageFlag = 2
        });

        secondSuccess.ShouldBeTrue();

        var secondRoot = secondReport.RootElement;
        secondRoot.GetProperty("totalCount").GetInt32().ShouldBe(2);
        secondRoot.GetProperty("hasMore").GetBoolean().ShouldBeFalse();

        var second = secondRoot.GetProperty("events")[0];
        second.GetProperty("sequence").GetInt64().ShouldBe(3);
        second.GetProperty("data").GetProperty("Member").GetString().ShouldBe("Sam");
    }

    /// <summary>
    ///     The honesty case the report shape exists for: a filter matching nothing is a REAL answer —
    ///     totalCount 0, no events, no error, success return — that a consumer can tell apart from a
    ///     crash without distinguishing "no output" from "no matches".
    /// </summary>
    [Fact]
    public async Task a_filter_matching_nothing_is_a_zero_answer_and_a_success()
    {
        var (success, report) = await runAsync(new EventQueryInput
        {
            EventTypeFlag = "no_such_event_type"
        });

        success.ShouldBeTrue();

        var root = report.RootElement;
        root.GetProperty("totalCount").GetInt32().ShouldBe(0);
        root.GetProperty("events").GetArrayLength().ShouldBe(0);
        root.TryGetProperty("error", out _).ShouldBeFalse();
    }

    /// <summary>
    ///     The other honesty case: this store was not configured to capture user_name, so the
    ///     jasperfx#737 guard rail refuses the filter by name, the command returns failure, and the
    ///     report still parses as one JSON shape with the error populated — never an empty result
    ///     that reads as "nobody's events".
    /// </summary>
    [Fact]
    public async Task an_unsupported_filter_is_refused_by_name_with_a_failure_return()
    {
        var (success, report) = await runAsync(new EventQueryInput
        {
            UserNameFlag = "nobody"
        });

        success.ShouldBeFalse();

        var root = report.RootElement;
        root.GetProperty("error").GetString()!.ShouldContain("UserName");
        root.GetProperty("totalCount").GetInt32().ShouldBe(0);
        root.GetProperty("events").GetArrayLength().ShouldBe(0);
    }
}
