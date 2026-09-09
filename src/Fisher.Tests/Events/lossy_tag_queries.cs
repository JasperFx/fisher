using JasperFx;
using JasperFx.Events;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Events;

public record ShipmentId(Guid Value);

public record CarrierCode(string Value);

public record DockNumber(int Value);

public record CargoLanded(string Species, int Weight);

/// <summary>
///     <c>EventQuery.TagValues</c> — the lossy name/value tag filter (fisher#230 / jasperfx#801).
/// </summary>
/// <remarks>
///     <para>
///         <c>EventQueryCompliance</c> owns the behaviour: the name spellings, the case-insensitivity,
///         the AND across entries and with every other filter, paging and <c>TotalCount</c>, and both
///         refusals. Twelve facts, and none of them is repeated here.
///     </para>
///     <para>
///         What is here is the two things that suite structurally cannot see. It registers a
///         Guid-valued and a string-valued tag and no numeric one, so an INTEGER tag column — a
///         different affinity path entirely, and one that fails by matching nothing — is uncovered.
///         And it cannot see <em>which index SQLite reaches</em>, which is the whole reason
///         <c>BindTagValue</c> exists: a blanket <c>collate nocase</c> answers every one of those
///         twelve facts correctly and scans the tag table doing it.
///     </para>
/// </remarks>
public class lossy_tag_queries : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("lossy-tag-queries");
    private DocumentStore _store = null!;

    private readonly ShipmentId _shipment = new(Guid.NewGuid());
    private readonly CarrierCode _carrier = new("Acme");
    private readonly DockNumber _dock = new(42);

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;

            // Suffixes deliberately unlike the CLR names, so a test naming one is naming that spelling.
            o.Events.RegisterTagType<ShipmentId>("shipment");
            o.Events.RegisterTagType<CarrierCode>("carrier");
            o.Events.RegisterTagType<DockNumber>("dock");
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();

        var loaded = session.Events.BuildEvent(new CargoLanded("Trout", 3));
        loaded.WithTag(_shipment, _carrier, _dock);
        session.Events.StartStream(Guid.NewGuid(), loaded);

        var untagged = session.Events.BuildEvent(new CargoLanded("Pike", 11));
        session.Events.StartStream(Guid.NewGuid(), untagged);

        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    ///     An INTEGER-valued tag matches on the string form of its number. The compliance suite
    ///     registers no numeric tag, and this is the one column type where the caller's value and the
    ///     stored value have different SQLite types — so it either converts or silently matches
    ///     nothing.
    /// </summary>
    [Fact]
    public async Task an_integer_valued_tag_matches_on_its_string_form()
    {
        await using var session = _store.LightweightSession();

        var result = await session.Events.QueryEventsAsync(
            new EventQuery { TagValues = { ["dock"] = "42" }, PageSize = 100 }, Token);

        result.TotalCount.ShouldBe(1);
        result.Events.Single().Data.ShouldBeOfType<CargoLanded>().Species.ShouldBe("Trout");

        var miss = await session.Events.QueryEventsAsync(
            new EventQuery { TagValues = { ["dock"] = "43" }, PageSize = 100 }, Token);

        miss.TotalCount.ShouldBe(0);
    }

    /// <summary>
    ///     A value that is not a number at all is an empty answer, not an error — the same rule the
    ///     suite pins for an unmatched Guid, on the column type where a conversion could have thrown.
    /// </summary>
    [Fact]
    public async Task an_unparseable_value_for_a_numeric_tag_is_an_empty_answer()
    {
        await using var session = _store.LightweightSession();

        var result = await session.Events.QueryEventsAsync(
            new EventQuery { TagValues = { ["dock"] = "not-a-number" }, PageSize = 100 }, Token);

        result.TotalCount.ShouldBe(0);
        result.Events.ShouldBeEmpty();
    }

    /// <summary>
    ///     A Guid tag's comparison is exact, so the tag table's <c>(value, seq_id)</c> primary key
    ///     serves the sub-select as a search rather than a scan.
    /// </summary>
    /// <remarks>
    ///     The pin on <c>BindTagValue</c>'s Guid arm, and the only assertion that can see it: the
    ///     caller's spelling is normalised to the lowercase canonical form
    ///     <c>EventTagWriter.ToDatabaseValue</c> wrote, so the comparison needs no collation override
    ///     and SQLite can reach the index. Replacing it with <c>collate nocase</c> answers the same
    ///     rows and reports <c>SCAN</c> here.
    /// </remarks>
    [Fact]
    public async Task a_guid_tag_lookup_is_served_by_the_tag_tables_primary_key()
    {
        var plan = await QueryPlanAsync(new EventQuery
        {
            TagValues = { ["shipment"] = _shipment.Value.ToString().ToUpperInvariant() }
        });

        plan.ShouldContain("fi_event_tag_shipment");
        plan.ShouldContain("SEARCH");
        plan.ShouldNotContain("SCAN fi_event_tag_shipment");
    }

    /// <summary>
    ///     A numeric tag reaches the index the same way, its value having been normalised to an
    ///     INTEGER rather than left as the caller's text.
    /// </summary>
    [Fact]
    public async Task a_numeric_tag_lookup_is_served_by_the_tag_tables_primary_key()
    {
        var plan = await QueryPlanAsync(new EventQuery { TagValues = { ["dock"] = "42" } });

        plan.ShouldContain("fi_event_tag_dock");
        plan.ShouldNotContain("SCAN fi_event_tag_dock");
    }

    /// <summary>
    ///     A string tag is the one that scans, and it is recorded rather than fixed: its stored text is
    ///     the application's own casing, so there is nothing to normalise the caller's spelling to and
    ///     <c>collate nocase</c> is what the contract's case-insensitivity costs. SQLite reaches an
    ///     index only under the index's own collation.
    /// </summary>
    [Fact]
    public async Task a_string_tag_lookup_pays_a_scan_for_its_case_insensitivity()
    {
        var plan = await QueryPlanAsync(new EventQuery { TagValues = { ["carrier"] = "ACME" } });

        plan.ShouldContain("SCAN fi_event_tag_carrier");

        // And it is genuinely case-insensitive, which is what the scan buys.
        await using var session = _store.LightweightSession();
        var result = await session.Events.QueryEventsAsync(
            new EventQuery { TagValues = { ["carrier"] = "ACME" }, PageSize = 100 }, Token);

        result.TotalCount.ShouldBe(1);
    }

    /// <summary>
    ///     The production <c>where</c> clause, through <c>explain query plan</c>. Rendered by
    ///     <c>EventOperations.AppendEventQueryFilters</c> itself rather than by a copy, because a plan
    ///     over a re-derivation would report about SQL the store does not run — the same reason
    ///     <c>ExplainAsync</c> takes the statement the query would run.
    /// </summary>
    private async Task<string> QueryPlanAsync(EventQuery query)
    {
        await using var session = _store.LightweightSession();

        var builder = new Weasel.Sqlite.CommandBuilder();
        builder.Append("explain query plan select seq_id from ");
        builder.Append(_store.Options.EventGraph.EventsTableName);

        session.Events.AppendEventQueryFilters(builder, query);

        var command = builder.Compile();

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);
        command.Connection = connection;

        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            lines.Add(reader.GetString(reader.GetOrdinal("detail")));
        }

        return string.Join(" | ", lines);
    }
}
