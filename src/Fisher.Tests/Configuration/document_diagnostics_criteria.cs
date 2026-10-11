using System.Text;
using JasperFx;
using JasperFx.Documents;
using JasperFx.Linq;

namespace Fisher.Tests.Configuration;

/// <summary>
///     jasperfx#869 on Fisher, beyond the shared <c>DocumentStoreDiagnosticsCompliance</c> facts: the
///     shapes Fisher's provider would translate SILENTLY WRONG, measured against a LINQ-to-objects oracle,
///     and the store-specific semantics a criteria read must keep.
/// </summary>
public class document_diagnostics_criteria : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("diag-criteria");
    private DocumentStore _store = null!;
    private List<CriteriaOrder> _orders = null!;

    private CancellationToken Token => TestContext.Current.CancellationToken;
    private IDocumentStoreDiagnostics Diagnostics => _store;
    private static readonly string OrderType = typeof(CriteriaOrder).FullName!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.Schema.For<CriteriaOrder>()
                .SoftDeleted()
                .Metadata(x =>
                {
                    x.CorrelationId.Enabled = true;
                    x.CausationId.Enabled = true;
                    x.LastModifiedBy.Enabled = true;
                });
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        _orders = Seed();

        await using var session = _store.LightweightSession();
        session.Store(_orders.ToArray());
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    /// <remarks>
    ///     Eight orders chosen so that every shape below selects some rows and not all of them — a shape
    ///     that matched nothing or everything could not tell a correct translation from a wrong one. One
    ///     <c>PlacedAt</c> sits at 23:30 on New Year's Eve at <c>-02:00</c>, which is already next year in
    ///     UTC: a translation that reads the instant rather than the offset's own calendar gets its
    ///     <c>Year</c> wrong.
    /// </remarks>
    private static List<CriteriaOrder> Seed() =>
    [
        Order("Alice", 100.50m, new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero), CriteriaStatus.Placed,
            "Austin", ["vip", "web"], [("A1", 2), ("B2", 7)], ["x", "y"], rush: true, priority: 1),
        Order("bob", 99.99m, new DateTimeOffset(2025, 11, 1, 8, 0, 0, TimeSpan.Zero), CriteriaStatus.Shipped,
            "Boston", ["web"], [("A1", 1)], ["x"], rush: false, priority: 2),
        Order("Carol", 250m, new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(2)), CriteriaStatus.Shipped,
            null, [], [("C3", 10), ("D4", 1), ("E5", 3)], [], rush: true, priority: 3, note: "fragile"),
        Order("Dave", 100.50m, new DateTimeOffset(2025, 12, 31, 23, 30, 0, TimeSpan.FromHours(-2)), CriteriaStatus.Cancelled,
            "Austin", ["vip"], [], ["z", "x"], rush: false, priority: 1),
        Order("Eve", 1000m, new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero), CriteriaStatus.Placed,
            "Chicago", ["vip", "gift", "web"], [("B2", 6)], ["x", "y", "z"], rush: false, priority: 5),
        Order("Frank", 5.25m, new DateTimeOffset(2026, 11, 15, 18, 45, 0, TimeSpan.Zero), CriteriaStatus.Shipped,
            "Austin", ["gift"], [("A1", 1), ("A1", 1)], ["y"], rush: true, priority: 2),
        Order("alice", 100.49m, new DateTimeOffset(2024, 3, 15, 9, 0, 0, TimeSpan.Zero), CriteriaStatus.Placed,
            "Dallas", [], [("Z9", 0)], [], rush: false, priority: 4, region: "TX"),
        Order("Gina", 100.51m, new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), CriteriaStatus.Cancelled,
            null, ["vip"], [("A1", 4)], ["q"], rush: false, priority: 3)
    ];

    private static CriteriaOrder Order(string customer, decimal total, DateTimeOffset placedAt, CriteriaStatus status,
        string? city, string[] tags, (string Sku, int Quantity)[] items, string[] codes, bool rush, int priority,
        string? note = null, string? region = null) => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = Guid.NewGuid(),
        CustomerName = customer,
        Total = total,
        PlacedAt = placedAt,
        DueDate = DateTime.SpecifyKind(placedAt.UtcDateTime.Date.AddDays(10).AddHours(6), DateTimeKind.Utc),
        ShipBy = DateOnly.FromDateTime(placedAt.UtcDateTime.AddDays(10)),
        Cutoff = new TimeOnly(placedAt.Hour % 12 + 6, placedAt.Minute),
        Window = TimeSpan.FromMinutes(priority * 45),
        Discount = priority switch { 1 => 5, 2 => 10, 3 => null, 4 => 0, _ => null },
        Status = status,
        ShipTo = city is null ? null : new CriteriaAddress { City = city, Region = region },
        Tags = tags.ToList(),
        Items = items.Select(x => new CriteriaLine { Sku = x.Sku, Quantity = x.Quantity }).ToList(),
        Codes = codes,
        IsRush = rush,
        Priority = priority,
        Note = note
    };

    // ---------------------------------------------------------------- the shape matrix

    /// <summary>
    ///     Every shape the console is likely to type, each run against the store and against LINQ to
    ///     objects over the same eight documents.
    /// </summary>
    private IEnumerable<Shape> Shapes()
    {
        var alice = _orders[0];
        var cutoff = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

        // date member access
        yield return new("DateTimeOffset.Year", "PlacedAt.Year = 2026");
        yield return new("DateTimeOffset.Month", "PlacedAt.Month = 3");
        yield return new("DateTimeOffset.Day", "PlacedAt.Day = 15");
        yield return new("DateTimeOffset.Date", "PlacedAt.Date = @0", [new DateTime(2026, 3, 15)]);
        yield return new("DateTime.Year", "DueDate.Year = 2026");
        yield return new("DateTime.Month", "DueDate.Month = 3");
        yield return new("DateTime.Day", "DueDate.Day = 25");
        yield return new("DateTime.Date", "DueDate.Date = @0", [new DateTime(2026, 3, 25, 0, 0, 0, DateTimeKind.Utc)]);
        yield return new("DateTime.Hour", "DueDate.Hour = 6");
        yield return new("DateOnly.Year", "ShipBy.Year = 2026");
        yield return new("DateOnly.Month", "ShipBy.Month = 3");
        yield return new("TimeOnly.Hour", "Cutoff.Hour = 9");
        yield return new("TimeSpan.Hours", "Window.Hours = 2");
        yield return new("TimeSpan.TotalHours", "Window.TotalHours > 2");
        yield return new("TimeOnly > @0", "Cutoff > @0", [new TimeOnly(9, 30)]);
        yield return new("TimeSpan > @0", "Window > @0", [TimeSpan.FromHours(2)]);

        // collection sizes
        yield return new("List.Count property", "Items.Count > 1");
        yield return new("List<string>.Count property", "Tags.Count > 1");
        yield return new("array Length", "Codes.Length = 2");
        yield return new("Count()", "Items.Count() > 1");
        yield return new("Count(pred)", "Items.Count(Quantity > 5) > 0");

        // collection predicates
        yield return new("Any()", "Tags.Any()");
        yield return new("Any(pred) on members", "Items.Any(Quantity > 5)");
        yield return new("Any(pred) string member", "Items.Any(Sku = \"A1\")");
        yield return new("All(pred)", "Items.All(Quantity > 0)");
        yield return new("Tags.Any(it = x)", "Tags.Any(it = \"vip\")");
        yield return new("Tags.Contains literal", "Tags.Contains(\"vip\")");
        yield return new("Tags.Contains @0", "Tags.Contains(@0)", ["gift"]);
        yield return new("array Contains", "Codes.Contains(\"z\")");

        // in
        yield return new("inline in (...)", "Priority in (1, 3)");
        yield return new("in @0", "Priority in @0", [new[] { 1, 3 }]);
        yield return new("string in @0", "CustomerName in @0", [new[] { "Alice", "Eve" }]);

        // decimal
        yield return new("decimal = literal", "Total = 100.50m");
        yield return new("decimal > literal", "Total > 100.50m");
        yield return new("decimal < literal", "Total < 100.50m");
        yield return new("decimal > int literal", "Total > 100");
        yield return new("decimal = @0", "Total = @0", [100.50m]);
        yield return new("decimal > @0", "Total > @0", [100.50m]);
        yield return new("decimal < @0", "Total < @0", [100.50m]);
        yield return new("decimal >= double literal", "Total >= 100.5");
        yield return new("decimal = @0 (JSON wire)", "Total = @0",
            System.Text.Json.JsonSerializer.Deserialize<object?[]>("[100.50]"));

        // enum
        yield return new("enum by name", "Status = \"Shipped\"");
        yield return new("enum by name !=", "Status != \"Placed\"");
        yield return new("enum by number", "Status = 1");
        yield return new("enum > number", "Status > 0");
        yield return new("enum in @0", "Status in @0", [new[] { CriteriaStatus.Placed, CriteriaStatus.Cancelled }]);
        yield return new("enum by lower-case name", "Status = \"shipped\"");

        // nested + null
        yield return new("nested member", "ShipTo.City = \"Austin\"")
            { Oracle = all => all.Where(x => x.ShipTo?.City == "Austin") };
        yield return new("nested null member", "ShipTo.Region = null")
            { Oracle = all => all.Where(x => x.ShipTo?.Region == null) };
        yield return new("nested member <>", "ShipTo.City <> \"Austin\"")
            { Oracle = all => all.Where(x => x.ShipTo?.City != "Austin") };
        yield return new("nested object null", "ShipTo = null");
        yield return new("nested object not null", "ShipTo != null");
        yield return new("string null", "Note = null");
        yield return new("string not null", "Note != null");
        yield return new("nullable string <>", "Note <> \"fragile\"");
        yield return new("nullable string not =", "not (Note = \"fragile\")");
        yield return new("nullable member <> via @0", "ShipTo.Region != @0", ["TX"])
            { Oracle = all => all.Where(x => x.ShipTo?.Region != "TX") };
        yield return new("not StartsWith non-null", "not CustomerName.StartsWith(\"A\")");
        yield return new("not Contains nullable", "not Note.Contains(\"frag\")")
            { Oracle = all => all.Where(x => !(x.Note?.Contains("frag") ?? false)) };
        yield return new("not Tags.Contains", "not Tags.Contains(\"vip\")");
        yield return new("not Items.Any(pred)", "not Items.Any(Quantity > 5)");
        yield return new("guarded <>", "Note <> \"fragile\" or Note = null");
        yield return new("guarded nested <>", "ShipTo.City <> \"Austin\" or ShipTo.City = null")
            { Oracle = all => all.Where(x => x.ShipTo?.City != "Austin") };
        yield return new("non-nullable int <>", "Priority <> 1");
        yield return new("nested <> null-safe compare", "ShipTo != null and ShipTo.City <> \"Austin\"");

        // strings
        yield return new("StartsWith", "CustomerName.StartsWith(\"A\")");
        yield return new("Contains", "CustomerName.Contains(\"li\")");
        yield return new("EndsWith", "CustomerName.EndsWith(\"e\")");
        yield return new("ToLower =", "CustomerName.ToLower() = \"alice\"");
        yield return new("ToUpper StartsWith", "CustomerName.ToUpper().StartsWith(\"A\")");
        yield return new("string <>", "CustomerName <> \"Alice\"");
        yield return new("string Length", "CustomerName.Length > 3");
        yield return new("nested string Length", "ShipTo.City.Length > 5")
            { Oracle = all => all.Where(x => x.ShipTo?.City.Length > 5) };

        // Nullable<T>
        yield return new("int? HasValue", "Discount.HasValue");
        yield return new("int?.Value >", "Discount.Value > 5")
            { Oracle = all => all.Where(x => x.Discount > 5) };
        yield return new("int? >", "Discount > 5");
        yield return new("int? = null", "Discount = null");
        yield return new("int? <>", "Discount <> 5");

        // bool
        yield return new("bool member", "IsRush");
        yield return new("bool = true", "IsRush = true");
        yield return new("not bool", "not IsRush");

        // Guid
        yield return new("Guid = @0", "CustomerId = @0", [alice.CustomerId]);
        yield return new("Guid = string @0", "CustomerId = @0", [alice.CustomerId.ToString()]);
        yield return new("Id = @0", "Id = @0", [alice.Id]);

        // dates by value
        yield return new("DateTimeOffset > @0", "PlacedAt > @0", [cutoff]);
        yield return new("DateTimeOffset >= @0 (offset)", "PlacedAt >= @0",
            [new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(2))]);
        yield return new("DateTimeOffset < string", "PlacedAt < \"2026-03-01\"");
        yield return new("DateTime > @0", "DueDate > @0", [new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)]);
        yield return new("DateTimeOffset > @0 (JSON wire)", "PlacedAt > @0",
            System.Text.Json.JsonSerializer.Deserialize<object?[]>("[\"2026-03-01T00:00:00Z\"]"));
        yield return new("DateOnly > @0", "ShipBy > @0", [new DateOnly(2026, 3, 1)]);
        yield return new("collection Count = 0", "Items.Count = 0");

        // arithmetic and composition
        yield return new("int arithmetic", "Priority * 2 > 5");
        yield return new("and / or", "(Priority > 2 and IsRush) or Status = \"Cancelled\"");

        // orderings, compared as sequences
        yield return new("order decimal desc", null, null, "Total desc");
        yield return new("order DateTimeOffset", null, null, "PlacedAt");
        // SQLite's BINARY collation orders strings ordinally (upper case first); LINQ to objects orders
        // them by culture. Neither is wrong — the oracle here is the ordinal order the store promises.
        yield return new("order string (ordinal)", null, null, "CustomerName")
            { Oracle = all => all.OrderBy(x => x.CustomerName, StringComparer.Ordinal).ThenBy(x => x.Id) };
        yield return new("order enum", null, null, "Status, Priority desc");
        yield return new("order nested", "ShipTo != null", null, "ShipTo.City desc");
        yield return new("order DateTime desc", null, null, "DueDate desc");
    }

    private sealed record Shape(string Label, string? Where, object?[]? Arguments = null, string? OrderBy = null)
    {
        /// <summary>
        ///     An explicit oracle where running the text over objects is not the answer a database gives —
        ///     null propagation through a nested member, and ordinal string ordering.
        /// </summary>
        public Func<IEnumerable<CriteriaOrder>, IEnumerable<CriteriaOrder>>? Oracle { get; init; }
    }

    private enum Verdict
    {
        Correct,
        Refused,
        SilentlyWrong,
        OracleCannotAnswer
    }

    private sealed record Measured(Shape Shape, string Oracle, string Store, Verdict Verdict);

    /// <summary>
    ///     The oracle: the same text through the same parser over the same documents, run by LINQ to
    ///     objects with the default policy — what the text MEANS, Fisher's refusals aside — or the shape's
    ///     explicit oracle where objects and a database legitimately differ.
    /// </summary>
    private (List<Guid>? Expected, string Label) RunOracle(Shape shape)
    {
        try
        {
            if (shape.Oracle is { } explicitOracle)
            {
                // Still parsed, so a shape the parser refuses is not given an answer it could not get.
                DynamicQuery.Validate(typeof(CriteriaOrder),
                    new DynamicQueryText(shape.Where, shape.OrderBy, shape.Arguments));
                var explicitIds = explicitOracle(_orders).Select(x => x.Id).ToList();
                return (explicitIds, explicitIds.Count + "*");
            }

            var text = new DynamicQueryText(shape.Where, shape.OrderBy is null ? null : shape.OrderBy + ", Id",
                shape.Arguments);
            var ids = DynamicQuery.Apply(_orders.AsQueryable(), text).Select(x => x.Id).ToList();
            return (ids, ids.Count.ToString());
        }
        catch (Exception e) when (e is DynamicQueryException or NullReferenceException)
        {
            return (null, e is NullReferenceException ? "n/a (null member)" : "refused by parser");
        }
    }

    private async Task<Measured> MeasureAsync(Shape shape)
    {
        var (expected, oracle) = RunOracle(shape);

        DocumentQueryResult result;
        try
        {
            result = await Diagnostics.QueryDocumentsAsync(OrderType,
                new DocumentQueryOptions(1, 100) { Where = shape.Where, OrderBy = shape.OrderBy, Arguments = shape.Arguments },
                Token);
        }
        catch (DocumentCriteriaNotSupportedException e)
        {
            return new Measured(shape, oracle, "refused: " + Trim(e.Message), Verdict.Refused);
        }

        var actual = result.Documents.Select(x => Guid.Parse(x.Id)).ToList();

        if (expected is null)
        {
            return new Measured(shape, oracle, actual.Count.ToString(), Verdict.OracleCannotAnswer);
        }

        var same = shape.OrderBy is null
            ? actual.Order().SequenceEqual(expected.Order()) && result.TotalCount == expected.Count
            : actual.SequenceEqual(expected);

        return new Measured(shape, oracle, $"{result.TotalCount}", same ? Verdict.Correct : Verdict.SilentlyWrong);
    }

    private static string Trim(string message) => message.Length > 90 ? message[..90] + "…" : message;

    /// <summary>
    ///     The pin on <see cref="DocumentStore.CriteriaPolicy" /> as a whole: every shape either matches the
    ///     oracle or is refused. A provider change that starts rendering one of them wrong fails here, and so
    ///     does a policy rule removed while its shape is still mistranslated.
    /// </summary>
    /// <remarks>
    ///     Set <c>FISHER_CRITERIA_MATRIX</c> to a file path to have the matrix written there as a markdown
    ///     table.
    /// </remarks>
    [Fact]
    public async Task no_criteria_shape_is_silently_wrong()
    {
        var measured = new List<Measured>();
        foreach (var shape in Shapes())
        {
            measured.Add(await MeasureAsync(shape));
        }

        var table = new StringBuilder("| shape | text | oracle n | store | verdict |\n|---|---|---|---|---|\n");
        foreach (var m in measured)
        {
            var text = (m.Shape.Where ?? "") + (m.Shape.OrderBy is null ? "" : $" order by {m.Shape.OrderBy}");
            table.AppendLine(
                $"| {m.Shape.Label} | `{text.Replace("|", "\\|")}` | {m.Oracle} | {m.Store.Replace("|", "\\|")} | {m.Verdict} |");
        }

        if (Environment.GetEnvironmentVariable("FISHER_CRITERIA_MATRIX") is { Length: > 0 } path)
        {
            await File.WriteAllTextAsync(path, table.ToString(), Token);
        }

        measured.Where(x => x.Verdict == Verdict.SilentlyWrong).ShouldBeEmpty(table.ToString());
    }

    // ---------------------------------------------------------------- each refusal rule, pinned

    private Task<DocumentQueryResult> WhereAsync(string where, params object?[] arguments)
        => Diagnostics.QueryDocumentsAsync(OrderType,
            new DocumentQueryOptions(1, 100) { Where = where, Arguments = arguments }, Token);

    [Theory]
    [InlineData("PlacedAt.Year = 2026")]
    [InlineData("DueDate.Month = 3")]
    [InlineData("ShipBy.Day = 25")]
    [InlineData("Cutoff.Hour = 9")]
    [InlineData("Window.TotalHours > 2")]
    public async Task a_part_of_a_date_or_time_is_refused(string where)
    {
        var refused = await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() => WhereAsync(where));

        refused.Criterion.ShouldBe(nameof(DocumentQueryOptions.Where));
        refused.Message.ShouldContain("Compare the whole value against a range");
    }

    [Fact]
    public async Task the_whole_date_compared_against_a_range_is_what_the_refusal_suggests_and_it_works()
    {
        var result = await WhereAsync("PlacedAt >= @0 and PlacedAt < @1",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        result.TotalCount.ShouldBe(_orders.Count(x => x.PlacedAt.UtcDateTime.Year == 2026));
    }

    [Fact]
    public async Task value_off_a_nullable_member_is_refused_and_the_member_itself_works()
    {
        var refused = await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() => WhereAsync("Discount.Value > 5"));
        refused.Message.ShouldContain("Compare the member itself");

        (await WhereAsync("Discount > 5")).TotalCount.ShouldBe(_orders.Count(x => x.Discount > 5));
        (await WhereAsync("Discount.HasValue")).TotalCount.ShouldBe(_orders.Count(x => x.Discount.HasValue));
    }

    [Theory]
    [InlineData("Note <> \"fragile\"", "Note")]
    [InlineData("not (Note = \"fragile\")", "Note")]
    [InlineData("not Note.StartsWith(\"f\")", "Note")]
    [InlineData("Discount <> 5", "Discount")]
    [InlineData("ShipTo.City <> \"Austin\"", "ShipTo")]
    [InlineData("ShipTo.City <> \"Austin\" or ShipTo = null and ShipTo.Region <> \"TX\"", "ShipTo")]
    [InlineData("Items.Any(Sku <> \"A1\") and ShipTo.Region <> \"TX\"", "ShipTo")]
    public async Task an_inequality_on_a_member_that_can_be_null_is_refused(string where, string member)
    {
        var refused = await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() => WhereAsync(where));

        refused.Criterion.ShouldBe(nameof(DocumentQueryOptions.Where));
        refused.Message.ShouldContain($"'{member}");
        refused.Message.ShouldContain("can be null");
    }

    /// <remarks>
    ///     The refusal's own advice, both ways, plus the members it must leave alone: a non-nullable value
    ///     type, and a string the document declares non-nullable.
    /// </remarks>
    [Fact]
    public async Task an_inequality_that_says_what_null_does_runs_and_matches_csharp()
    {
        (await WhereAsync("Note <> \"fragile\" or Note = null")).TotalCount
            .ShouldBe(_orders.Count(x => x.Note != "fragile"));

        (await WhereAsync("Note != null and Note <> \"fragile\"")).TotalCount
            .ShouldBe(_orders.Count(x => x.Note != null && x.Note != "fragile"));

        (await WhereAsync("not (Note = \"fragile\" or Note = null)")).TotalCount
            .ShouldBe(_orders.Count(x => !(x.Note == "fragile" || x.Note == null)));

        (await WhereAsync("ShipTo.City <> \"Austin\" or ShipTo.City = null")).TotalCount
            .ShouldBe(_orders.Count(x => x.ShipTo?.City != "Austin"));

        // ShipTo is the only nullable link — City is declared non-nullable — so guarding it is enough.
        (await WhereAsync("ShipTo != null and ShipTo.City <> \"Austin\"")).TotalCount
            .ShouldBe(_orders.Count(x => x.ShipTo != null && x.ShipTo.City != "Austin"));

        (await WhereAsync("Discount <> 5 or Discount = null")).TotalCount
            .ShouldBe(_orders.Count(x => x.Discount != 5));

        (await WhereAsync("Priority <> 1")).TotalCount.ShouldBe(_orders.Count(x => x.Priority != 1));
        (await WhereAsync("CustomerName <> \"Alice\"")).TotalCount.ShouldBe(_orders.Count(x => x.CustomerName != "Alice"));
        (await WhereAsync("Status <> \"Placed\"")).TotalCount.ShouldBe(_orders.Count(x => x.Status != CriteriaStatus.Placed));
    }

    [Fact]
    public async Task a_shape_the_provider_cannot_translate_is_a_refusal_not_a_failure()
    {
        var refused = await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() => WhereAsync("Priority * 2 > 5"));

        refused.Criterion.ShouldBe(nameof(DocumentQueryOptions.Where));
        refused.Message.ShouldContain("cannot translate");
        refused.InnerException.ShouldNotBeNull();
    }

    [Fact]
    public async Task a_cancelled_read_is_cancelled_not_refused()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Diagnostics.QueryDocumentsAsync(OrderType,
            new DocumentQueryOptions(1, 10) { Where = "Priority > @0", Arguments = [1] }, cancelled.Token));
    }

    // ---------------------------------------------------------------- what a criteria read still is

    /// <remarks>
    ///     The point of selecting from the provider's statement rather than materializing documents: the row a
    ///     criteria page returns is the row the hand-built read returns — the same byte-exact JSON and the
    ///     same metadata — so a console cannot tell which path served it.
    /// </remarks>
    [Fact]
    public async Task a_criteria_row_is_the_stored_row_byte_for_byte()
    {
        var alice = _orders[0];

        var plain = await Diagnostics.QueryDocumentsAsync(OrderType,
            new DocumentQueryOptions(1, 10, alice.Id.ToString()), Token);
        var filtered = await Diagnostics.QueryDocumentsAsync(OrderType,
            new DocumentQueryOptions(1, 10) { Where = "Id = @0", Arguments = [alice.Id] }, Token);

        var expected = plain.Documents.ShouldHaveSingleItem();
        var actual = filtered.Documents.ShouldHaveSingleItem();

        actual.ShouldBe(expected);
        actual.Json.ShouldBe(expected.Json);
        actual.Version.ShouldBe(expected.Version);
        actual.LastModified.ShouldNotBeNull();
        actual.DocumentType.ShouldBe(typeof(CriteriaOrder).FullName);
    }

    [Fact]
    public async Task where_combines_with_the_metadata_filters()
    {
        var tagged = new[] { Order("Hal", 1m, DateTimeOffset.UtcNow, CriteriaStatus.Placed, "Austin", [], [], [], false, 7),
            Order("Ivy", 2m, DateTimeOffset.UtcNow, CriteriaStatus.Placed, "Austin", [], [], [], false, 8) };

        await using (var session = _store.LightweightSession())
        {
            session.CorrelationId = "corr-869";
            session.CausationId = "cause-869";
            session.LastModifiedBy = "sam";
            session.Store(tagged);
            await session.SaveChangesAsync(Token);
        }

        var both = new DocumentQueryOptions(1, 10)
        {
            Where = "Priority > @0", Arguments = [7], CorrelationId = "corr-869", CausationId = "cause-869",
            LastModifiedBy = "sam"
        };

        var result = await Diagnostics.QueryDocumentsAsync(OrderType, both, Token);
        result.TotalCount.ShouldBe(1);
        result.Documents.ShouldHaveSingleItem().Id.ShouldBe(tagged[1].Id.ToString());

        // The metadata filter alone would have matched both; the Where alone matches nothing else either,
        // so the page above is the intersection rather than either one.
        (await Diagnostics.QueryDocumentsAsync(OrderType, both with { Where = "Priority > @0", Arguments = [0] }, Token))
            .TotalCount.ShouldBe(2);
        (await Diagnostics.QueryDocumentsAsync(OrderType, both with { CorrelationId = "someone-else" }, Token))
            .TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task where_and_include_soft_deleted()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Delete(_orders[0]); // Alice, priority 1
            await session.SaveChangesAsync(Token);
        }

        var byPriority = new DocumentQueryOptions(1, 10) { Where = "Priority = @0", Arguments = [1] };

        var live = await Diagnostics.QueryDocumentsAsync(OrderType, byPriority, Token);
        live.Documents.ShouldHaveSingleItem().Id.ShouldBe(_orders[3].Id.ToString());

        var withDeleted = await Diagnostics.QueryDocumentsAsync(OrderType, byPriority with { IncludeSoftDeleted = true }, Token);
        withDeleted.TotalCount.ShouldBe(2);
        withDeleted.Documents.Single(x => x.Id == _orders[0].Id.ToString()).IsDeleted.ShouldBeTrue();
    }

    [Fact]
    public async Task a_where_without_an_ordering_pages_by_id_and_the_total_is_the_filtered_total()
    {
        var matching = _orders.Where(x => x.Priority < 4).ToList();

        var pages = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await Diagnostics.QueryDocumentsAsync(OrderType,
                new DocumentQueryOptions(page, 2) { Where = "Priority < @0", Arguments = [4] }, Token);

            result.TotalCount.ShouldBe(matching.Count);
            pages.AddRange(result.Documents.Select(x => x.Id));
        }

        pages.ShouldBe(matching.Select(x => x.Id.ToString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task an_ordering_alone_still_reads_every_row()
    {
        var result = await Diagnostics.QueryDocumentsAsync(OrderType,
            new DocumentQueryOptions(1, 3) { OrderBy = "Total desc" }, Token);

        result.TotalCount.ShouldBe(_orders.Count);
        result.Documents.Select(x => x.Id)
            .ShouldBe(_orders.OrderByDescending(x => x.Total).ThenBy(x => x.Id.ToString(), StringComparer.Ordinal)
                .Take(3).Select(x => x.Id.ToString()));
    }
}

public enum CriteriaStatus
{
    Placed,
    Shipped,
    Cancelled
}

public class CriteriaAddress
{
    public string City { get; set; } = string.Empty;
    public string? Region { get; set; }
}

public class CriteriaLine
{
    public string Sku { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

public class CriteriaOrder
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public DateTimeOffset PlacedAt { get; set; }
    public DateTime DueDate { get; set; }
    public DateOnly ShipBy { get; set; }
    public TimeOnly Cutoff { get; set; }
    public TimeSpan Window { get; set; }
    public int? Discount { get; set; }
    public CriteriaStatus Status { get; set; }
    public CriteriaAddress? ShipTo { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<CriteriaLine> Items { get; set; } = [];
    public string[] Codes { get; set; } = [];
    public bool IsRush { get; set; }
    public int Priority { get; set; }
    public string? Note { get; set; }
}
