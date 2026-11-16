using Fisher.Events.Daemon;
using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events;
using Microsoft.Data.Sqlite;
using Weasel.Storage;

namespace Fisher.Tests.Daemon;

/// <summary>
///     fisher#583: the daemon's event-type allow list is bound as one <c>json_each</c> parameter once it is
///     long, the contract weasel#846 put on <see cref="IEventPagingDialect" />.
/// </summary>
/// <remarks>
///     The loader filters only in SQL, so a list the provider refuses fails the shard. One parameter per
///     alias stops at SQLite's 32,766 variables. No real event registry produces a list that long, so these
///     facts build one through a stub registry and drive the dialect's two commands against a real
///     <c>fi_events</c>. The page and the skip-ahead probe share the filter, and both are checked.
/// </remarks>
public class event_type_allow_list_binding : IAsyncLifetime
{
    // Past SQLite's 32,766-variable ceiling, which is what one parameter per alias cannot get past.
    private const int LongerThanSqliteAllows = 33_000;

    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("allowlist");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = await StoreAsync(options =>
        {
            options.Events.AddEventType(typeof(LanternLit));
            options.Events.AddEventType(typeof(LanternDimmed));
            options.Events.AddEventType(typeof(LanternKindled));
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<DocumentStore> StoreAsync(Action<StoreOptions> configure)
    {
        var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            configure(options);
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        return store;
    }

    [Fact]
    public async Task a_list_longer_than_sqlite_allows_pages_exactly_the_matching_rows()
    {
        var lit = await AppendAsync(new LanternLit(Guid.NewGuid(), "Beregond"));
        await AppendAsync(new LanternDimmed(Guid.NewGuid(), 40));
        var kindled = await AppendAsync(new LanternKindled(Guid.NewGuid(), "Bergil", "Lit"));

        var allowList = LongAllowList(AliasOf<LanternLit>(), AliasOf<LanternKindled>());
        allowList.AliasesIncludingUpcastSources.Count.ShouldBeGreaterThan(32_766);

        var dialect = new SqliteEventPagingDialect(_store.Options);
        var query = Query(allowList);

        var page = dialect.BuildPageCommand(query);
        (await SequencesAsync(page)).ShouldBe([lit, kindled]);

        var probe = dialect.BuildNextMatchingSequenceCommand(query);
        (await SequencesAsync(probe)).ShouldBe([lit]);

        // floor, ceiling, the list and the batch size: a fixed count, whatever the list's length.
        page.Parameters.Count.ShouldBe(4);
        probe.Parameters.Count.ShouldBe(3);
        page.Sql.ShouldContain("json_each(@types)");
    }

    /// <summary>
    ///     The skip-ahead probe answers past a run of non-matching rows, under the same filter as the page.
    /// </summary>
    [Fact]
    public async Task the_probe_skips_rows_the_long_list_does_not_admit()
    {
        await AppendAsync(new LanternDimmed(Guid.NewGuid(), 10));
        await AppendAsync(new LanternDimmed(Guid.NewGuid(), 20));
        var kindled = await AppendAsync(new LanternKindled(Guid.NewGuid(), "Bergil", "Lit"));

        var dialect = new SqliteEventPagingDialect(_store.Options);
        var probe = dialect.BuildNextMatchingSequenceCommand(Query(LongAllowList(AliasOf<LanternKindled>())));

        (await SequencesAsync(probe)).ShouldBe([kindled]);
    }

    [Fact]
    public void a_short_list_keeps_one_parameter_per_alias()
    {
        var allowList = EventTypeAllowList.For(_store.Options.EventGraph, [typeof(LanternLit), typeof(LanternKindled)]);

        var page = new SqliteEventPagingDialect(_store.Options).BuildPageCommand(Query(allowList));

        page.Sql.ShouldContain("type in (@t0, @t1)");
        page.Sql.ShouldNotContain("json_each");
    }

    /// <summary>
    ///     fisher#191 holds on the <c>json_each</c> form too: a filter on the upcast TARGET still admits the rows
    ///     written under the source's old name.
    /// </summary>
    [Fact]
    public async Task upcast_source_names_reach_the_json_each_form()
    {
        await _store.DisposeAsync();

        // The rows the pre-migration application wrote, under the old name.
        long legacy;
        await using (var before = await StoreAsync(options => options.Events.AddEventType(typeof(LanternLit))))
        {
            await using var session = before.LightweightSession();
            session.Events.StartStream(Guid.NewGuid(), new LanternLit(Guid.NewGuid(), "Beregond"));
            await session.SaveChangesAsync(Token);
            legacy = (await LastSequenceAsync(before))!.Value;
        }

        _store = await StoreAsync(options => options.Events.Upcasters.Upcast<LanternLit, LanternKindled>(
            old => new LanternKindled(old.LanternId, old.Keeper, "Lit")));

        var allowList = EventTypeAllowList.For(_store.Options.EventGraph, [typeof(LanternKindled)]);
        allowList.AliasesIncludingUpcastSources.ShouldContain(AliasOf<LanternLit>());

        // A threshold of zero drives the json_each form with the list real types produce.
        var page = new SqliteEventPagingDialect(_store.Options, jsonArrayThreshold: 0).BuildPageCommand(Query(allowList));

        page.Sql.ShouldContain("json_each(@types)");
        (await SequencesAsync(page)).ShouldBe([legacy]);
    }

    /// <summary>
    ///     The json_each subquery does not change how <c>fi_events</c> is reached: SQLite reports the same step
    ///     for both forms, and it is a search by the sequence range, not a scan.
    /// </summary>
    [Fact]
    public async Task both_forms_reach_fi_events_the_same_way()
    {
        var allowList = EventTypeAllowList.For(_store.Options.EventGraph, [typeof(LanternLit), typeof(LanternKindled)]);
        var query = Query(allowList);

        var perAlias = await EventsTableStepsAsync(new SqliteEventPagingDialect(_store.Options).BuildPageCommand(query));
        var jsonEach = await EventsTableStepsAsync(
            new SqliteEventPagingDialect(_store.Options, jsonArrayThreshold: 0).BuildPageCommand(query));

        jsonEach.ShouldBe(perAlias);
        jsonEach.ShouldAllBe(step => step.StartsWith("SEARCH"));
    }

    private string AliasOf<T>() => _store.Options.EventGraph.EventMappingFor(typeof(T)).EventTypeName;

    private static EventPageQuery Query(EventTypeAllowList allowList) => new()
    {
        Floor = 0,
        Ceiling = long.MaxValue,
        BatchSize = 100,
        EventTypes = allowList
    };

    /// <summary>
    ///     <paramref name="realAliases" /> spread through filler names, past SQLite's variable ceiling.
    /// </summary>
    private static EventTypeAllowList LongAllowList(params string[] realAliases)
    {
        var aliases = Enumerable.Range(0, LongerThanSqliteAllows).Select(i => $"filler_{i:D5}").ToList();
        for (var i = 0; i < realAliases.Length; i++)
        {
            aliases.Insert((i + 1) * aliases.Count / (realAliases.Length + 1), realAliases[i]);
        }

        return EventTypeAllowList.For(new SequentialAliasRegistry(aliases), [.. aliases.Select(_ => typeof(Placeholder))]);
    }

    private async Task<long> AppendAsync(object @event)
    {
        await using var session = _store.LightweightSession();
        session.Events.StartStream(Guid.NewGuid(), @event);
        await session.SaveChangesAsync(Token);

        return (await LastSequenceAsync(_store))!.Value;
    }

    private static async Task<long?> LastSequenceAsync(DocumentStore store)
    {
        await using var connection = await store.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "select max(seq_id) from fi_events";

        return await command.ExecuteScalarAsync(Token) is long sequence ? sequence : null;
    }

    private async Task<List<long>> SequencesAsync(EventPageCommand page)
    {
        await using var connection = await _store.Database.OpenConnectionAsync(Token);
        await using var command = CommandFor(connection, page.Sql, page);

        var sequences = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            sequences.Add(reader.GetInt64(0));
        }

        return sequences;
    }

    private async Task<List<string>> EventsTableStepsAsync(EventPageCommand page)
    {
        await using var connection = await _store.Database.OpenConnectionAsync(Token);
        await using var command = CommandFor(connection, "explain query plan " + page.Sql, page);

        var steps = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            var detail = reader.GetString(3);
            if (detail.Contains("fi_events", StringComparison.Ordinal))
            {
                steps.Add(detail);
            }
        }

        steps.ShouldNotBeEmpty();
        return steps;
    }

    private static SqliteCommand CommandFor(SqliteConnection connection, string sql, EventPageCommand page)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in page.Parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        return command;
    }

    // Concrete on purpose: an interface, an abstract type or object turns the whole list into "admit
    // everything" (weasel#845), which would test nothing.
    private sealed record Placeholder;

    /// <summary>
    ///     Hands out the next alias on every lookup, so one placeholder type yields as long a list as a
    ///     test needs. <see cref="EventTypeAllowList.For(IEventRegistry, IReadOnlyList{Type})" /> asks once
    ///     per type, in order.
    /// </summary>
    private sealed class SequentialAliasRegistry(IReadOnlyList<string> aliases) : EventRegistry
    {
        private int _next;

        public override IEventType EventMappingFor(Type eventType)
        {
            var alias = aliases[_next++];
            return new EventTypeData<object> { EventTypeName = alias, DotNetTypeName = $"dotnet::{alias}" };
        }
    }
}
