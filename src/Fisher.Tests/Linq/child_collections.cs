using System.Linq.Expressions;
using Fisher.Linq;
using Fisher.Linq.Members;
using Fisher.Linq.Parsing;
using JasperFx;
using Weasel.Core;

namespace Fisher.Tests.Linq;

public class Expedition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public int[] Depths { get; set; } = [];
    public List<Grade> Grades { get; set; } = [];
    public List<Guid> CrewIds { get; set; } = [];
    public List<Stop> Stops { get; set; } = [];
}

public class Stop
{
    public string Port { get; set; } = "";
    public int Days { get; set; }
    public bool Resupplied { get; set; }
    public List<string> Cargo { get; set; } = [];
}

/// <summary>
///     Child-collection predicates over <c>json_each</c>: <c>Contains</c>, <c>Any</c>, <c>All</c> and
///     <c>Count</c> comparisons, asserted both as generated SQL and by running against documents the
///     store actually wrote.
/// </summary>
/// <remarks>
///     <para>
///         The seeded set deliberately includes the three degenerate storage shapes: a populated
///         collection, an empty one, and a member stored as JSON <b>null</b>. The null one is the case
///         that silently corrupts a naive translation — <c>json_each</c> over a null member yields one
///         phantom row where an absent key yields zero — so almost every executed assertion here is
///         discriminating against exactly that.
///     </para>
///     <para>
///         The refusal tests are as load-bearing as the happy paths. Fisher's house rule is refuse
///         rather than silently mis-translate, and each refused shape below is one that would
///         otherwise produce plausible SQL reading the wrong JSON.
///     </para>
/// </remarks>
public class child_collections : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("children");
    private DocumentStore _store = null!;
    private readonly Guid _shackletonId = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Expedition>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = _store.LightweightSession();
        session.Store(new Expedition
        {
            Id = Guid.NewGuid(), Name = "Endurance",
            Tags = ["urgent", "cold"],
            Depths = [10, 250],
            Grades = [Grade.HighDistinction],
            CrewIds = [_shackletonId],
            Stops =
            [
                new Stop { Port = "Oslo", Days = 3, Resupplied = true, Cargo = ["fuel"] },
                new Stop { Port = "Reykjavik", Days = 1, Resupplied = false, Cargo = [] }
            ]
        });
        session.Store(new Expedition
        {
            Id = Guid.NewGuid(), Name = "Beagle",
            Tags = ["survey", null!],
            Depths = [5],
            Grades = [Grade.Pass],
            Stops =
            [
                new Stop { Port = "Plymouth", Days = 10, Resupplied = true, Cargo = ["specimens", "fuel"] },
                // A null Port: the element for which a string predicate is NULL rather than
                // true or false — the three-valued-logic shape the All() tests discriminate on.
                new Stop { Port = null!, Days = 0, Resupplied = true, Cargo = [] }
            ]
        });
        session.Store(new Expedition
        {
            Id = Guid.NewGuid(), Name = "Fram",
            Tags = null!,      // stored as JSON null — the phantom-row shape
            Depths = [],       // stored as an empty array
            Grades = [],
            Stops = null!      // JSON null again, for the child-object operators
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private string SqlFor(Expression<Func<Expedition, bool>> predicate)
    {
        var factory = new MemberFactory(_store.Options, _store.Options.Schema.For<Expedition>().Mapping);
        var builder = new Weasel.Sqlite.CommandBuilder();
        new WhereClauseParser(factory).Parse(predicate.Body).Apply(builder);
        return builder.Compile().CommandText;
    }

    private async Task<List<string>> NamesMatchingAsync(Expression<Func<Expedition, bool>> predicate)
    {
        var mapping = _store.Options.Schema.For<Expedition>().Mapping;
        var factory = new MemberFactory(_store.Options, mapping);

        var builder = new Weasel.Sqlite.CommandBuilder();
        builder.Append($"select json_extract(data, '$.name') from {mapping.QuotedTableName} where ");
        new WhereClauseParser(factory).Parse(predicate.Body).Apply(builder);

        await using var connection = await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var command = builder.Compile();
        command.Connection = connection;

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    [Fact]
    public async Task contains_on_a_scalar_collection_is_a_correlated_exists()
    {
        SqlFor(x => x.Tags.Contains("urgent")).ShouldBe(
            "exists (select 1 from json_each(data, '$.tags') as each_1 "
            + "where each_1.key is not null and each_1.value = @p0)");

        (await NamesMatchingAsync(x => x.Tags.Contains("urgent"))).ShouldBe(["Endurance"]);
    }

    /// <summary>
    ///     An array member reaches the static <c>Enumerable.Contains</c> form rather than the
    ///     instance one, and translates the same way.
    /// </summary>
    [Fact]
    public async Task contains_on_an_int_array()
    {
        (await NamesMatchingAsync(x => x.Depths.Contains(250))).ShouldBe(["Endurance"]);
        (await NamesMatchingAsync(x => x.Depths.Contains(99))).ShouldBeEmpty();
    }

    /// <summary>
    ///     Guid elements go through the element member's conversion, matching the lowercase canonical
    ///     text System.Text.Json wrote — the same trap every other Guid comparison has to dodge.
    /// </summary>
    [Fact]
    public async Task contains_converts_guid_elements_to_their_stored_text()
    {
        (await NamesMatchingAsync(x => x.CrewIds.Contains(_shackletonId))).ShouldBe(["Endurance"]);
    }

    /// <summary>
    ///     Under the default <c>EnumStorage.AsInteger</c> the elements are JSON numbers.
    /// </summary>
    [Fact]
    public async Task contains_on_an_enum_collection_stored_as_integers()
    {
        (await NamesMatchingAsync(x => x.Grades.Contains(Grade.HighDistinction))).ShouldBe(["Endurance"]);
        (await NamesMatchingAsync(x => x.Grades.Contains(Grade.Pass))).ShouldBe(["Beagle"]);
    }

    /// <summary>
    ///     Under <c>AsString</c> the stored element is the member's <em>name through the naming
    ///     policy</em> — "highDistinction", not "HighDistinction" — so this test fails against any
    ///     translation that does not route the value through <see cref="EnumMember" />'s conversion.
    /// </summary>
    [Fact]
    public async Task contains_on_an_enum_collection_stored_as_strings()
    {
        using var database = TemporaryDatabase.Create("children_enum");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.ConfigureSerialization(EnumStorage.AsString);
            options.Schema.For<Expedition>();
        });
        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = store.LightweightSession();
        session.Store(new Expedition { Id = Guid.NewGuid(), Name = "Terror", Grades = [Grade.HighDistinction] });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var matches = await session.Query<Expedition>()
            .Where(x => x.Grades.Contains(Grade.HighDistinction))
            .ToListAsync(TestContext.Current.CancellationToken);

        matches.Select(x => x.Name).ShouldBe(["Terror"]);
    }

    /// <summary>
    ///     A member stored as JSON null contains nothing — including null. The discriminating half is
    ///     Beagle, whose array genuinely holds a null element and must still match, which is exactly
    ///     what the <c>key is not null</c> guard has to let through while blocking Fram's phantom row.
    /// </summary>
    [Fact]
    public async Task contains_null_matches_a_null_element_but_not_a_null_collection()
    {
        (await NamesMatchingAsync(x => x.Tags.Contains(null!))).ShouldBe(["Beagle"]);
    }

    [Fact]
    public async Task bare_any_asks_whether_the_collection_holds_anything()
    {
        SqlFor(x => x.Tags.Any()).ShouldBe(
            "exists (select 1 from json_each(data, '$.tags') as each_1 where each_1.key is not null)");

        // Fram's null Tags and empty Depths are both "nothing there".
        (await NamesMatchingAsync(x => x.Tags.Any())).ShouldBe(["Beagle", "Endurance"]);
        (await NamesMatchingAsync(x => x.Depths.Any())).ShouldBe(["Beagle", "Endurance"]);
        (await NamesMatchingAsync(x => !x.Tags.Any())).ShouldBe(["Fram"]);
    }

    [Fact]
    public async Task count_comparisons_use_a_count_subquery()
    {
        SqlFor(x => x.Tags.Count() > 1).ShouldBe(
            "(select count(*) from json_each(data, '$.tags') as each_1 "
            + "where each_1.key is not null) > @p0");

        (await NamesMatchingAsync(x => x.Tags.Count() > 1)).ShouldBe(["Beagle", "Endurance"]);
        (await NamesMatchingAsync(x => x.Tags.Count() == 2)).ShouldBe(["Beagle", "Endurance"]);
    }

    [Fact]
    public async Task a_reversed_count_comparison_reverses_the_operator()
    {
        (await NamesMatchingAsync(x => 1 < x.Tags.Count())).ShouldBe(["Beagle", "Endurance"]);
    }

    /// <summary>
    ///     The <c>List.Count</c> property and an array's <c>Length</c> are the same question asked
    ///     without the method call, and translate identically. String <c>Length</c> keeps its own
    ///     <c>length()</c> translation.
    /// </summary>
    [Fact]
    public async Task the_count_property_and_array_length_translate_like_count()
    {
        (await NamesMatchingAsync(x => x.Tags.Count > 1)).ShouldBe(["Beagle", "Endurance"]);
        (await NamesMatchingAsync(x => x.Depths.Length == 2)).ShouldBe(["Endurance"]);

        SqlFor(x => x.Name.Length == 5).ShouldBe("length(json_extract(data, '$.name')) = @p0");
    }

    /// <summary>
    ///     An absent member and one stored as JSON null both count as zero — the same honest reading
    ///     <c>IsEmpty()</c> documents — where in-memory LINQ over a null collection would throw. The
    ///     phantom <c>json_each</c> row must not make a null collection count 1.
    /// </summary>
    [Fact]
    public async Task a_null_or_empty_collection_counts_as_zero()
    {
        (await NamesMatchingAsync(x => x.Tags.Count() == 0)).ShouldBe(["Fram"]);
        (await NamesMatchingAsync(x => x.Depths.Count() == 0)).ShouldBe(["Fram"]);
    }

    [Fact]
    public async Task count_with_a_predicate_counts_matching_elements_only()
    {
        (await NamesMatchingAsync(x => x.Stops.Count(s => s.Resupplied) == 1)).ShouldBe(["Endurance"]);
        (await NamesMatchingAsync(x => x.Stops.Count(s => s.Resupplied) == 2)).ShouldBe(["Beagle"]);
        (await NamesMatchingAsync(x => x.Stops.Count(s => s.Days < 5) == 2)).ShouldBe(["Endurance"]);
    }

    [Fact]
    public async Task any_with_a_child_predicate_extracts_from_the_element()
    {
        SqlFor(x => x.Stops.Any(s => s.Port == "Oslo")).ShouldBe(
            "exists (select 1 from json_each(data, '$.stops') as each_1 "
            + "where each_1.key is not null and json_extract(each_1.value, '$.port') = @p0)");

        (await NamesMatchingAsync(x => x.Stops.Any(s => s.Port == "Oslo"))).ShouldBe(["Endurance"]);
        (await NamesMatchingAsync(x => x.Stops.Any(s => s.Days > 5))).ShouldBe(["Beagle"]);
        (await NamesMatchingAsync(x => x.Stops.Any(s => s.Resupplied))).ShouldBe(["Beagle", "Endurance"]);
        (await NamesMatchingAsync(x => x.Stops.Any(s => s.Port == "Oslo" && s.Days == 3)))
            .ShouldBe(["Endurance"]);
    }

    /// <summary>
    ///     A collection predicate inside a collection predicate gets a fresh <c>json_each</c> alias
    ///     one depth down, so the two do not collide.
    /// </summary>
    [Fact]
    public async Task a_nested_collection_predicate_aliases_one_depth_deeper()
    {
        SqlFor(x => x.Stops.Any(s => s.Cargo.Contains("fuel"))).ShouldBe(
            "exists (select 1 from json_each(data, '$.stops') as each_1 "
            + "where each_1.key is not null and "
            + "exists (select 1 from json_each(each_1.value, '$.cargo') as each_2 "
            + "where each_2.key is not null and each_2.value = @p0))");

        (await NamesMatchingAsync(x => x.Stops.Any(s => s.Cargo.Contains("fuel"))))
            .ShouldBe(["Beagle", "Endurance"]);
        (await NamesMatchingAsync(x => x.Stops.Any(s => s.Cargo.Contains("specimens"))))
            .ShouldBe(["Beagle"]);
    }

    /// <summary>
    ///     <c>All</c> is vacuously true over an empty, absent or null collection — the same answer
    ///     <c>Enumerable.All</c> gives an empty sequence — which is why Fram appears in both results.
    /// </summary>
    [Fact]
    public async Task all_is_a_negated_exists_and_vacuously_true_when_empty()
    {
        (await NamesMatchingAsync(x => x.Stops.All(s => s.Days < 5))).ShouldBe(["Endurance", "Fram"]);
        (await NamesMatchingAsync(x => x.Stops.All(s => s.Resupplied))).ShouldBe(["Beagle", "Fram"]);
    }

    /// <summary>
    ///     SQL three-valued logic must not let an element slip past <c>All</c>: an element for which
    ///     the predicate is NULL has not <em>satisfied</em> it. Beagle's null-Port stop is that shape
    ///     — <c>NULL != 'Oslo'</c> is NULL, and a naive <c>not exists (… not (p))</c> would read that
    ///     as passing.
    /// </summary>
    /// <remarks>
    ///     This follows SQL null semantics rather than the CLR's, deliberately and consistently with
    ///     the rest of the provider: a document whose <c>Name</c> is null does not match
    ///     <c>x.Name != "Oslo"</c> either. In-memory LINQ would say Beagle passes, because C#'s
    ///     <c>null != "Oslo"</c> is true.
    /// </remarks>
    [Fact]
    public async Task an_element_the_predicate_cannot_decide_fails_all()
    {
        (await NamesMatchingAsync(x => x.Stops.All(s => s.Port != "Oslo"))).ShouldBe(["Fram"]);
    }

    /// <summary>
    ///     The previously-advertised docs examples, end to end through the real query provider.
    /// </summary>
    [Fact]
    public async Task the_documented_collection_examples_work_through_the_provider()
    {
        await using var session = _store.QuerySession();

        (await session.Query<Expedition>().Where(x => x.Tags.Contains("urgent"))
                .ToListAsync(TestContext.Current.CancellationToken))
            .Select(x => x.Name).ShouldBe(["Endurance"]);

        (await session.Query<Expedition>().Where(x => x.Tags.Any())
                .CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);

        (await session.Query<Expedition>().Where(x => x.Tags.Count() > 2)
                .ToListAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();

        (await session.Query<Expedition>().Where(x => x.Stops.Any(s => s.Days > 5))
                .ToListAsync(TestContext.Current.CancellationToken))
            .Select(x => x.Name).ShouldBe(["Beagle"]);
    }

    [Fact]
    public void a_predicate_referencing_the_outer_document_is_refused()
    {
        var ex = Should.Throw<BadLinqExpressionException>(
            () => SqlFor(x => x.Stops.Any(s => s.Port == x.Name)));

        ex.Message.ShouldContain("outside the collection element's scope");
    }

    [Fact]
    public void a_member_access_on_a_scalar_element_is_refused()
    {
        var ex = Should.Throw<BadLinqExpressionException>(
            () => SqlFor(x => x.Tags.Any(t => t.Length > 3)));

        ex.Message.ShouldContain("no members to extract");
    }

    /// <summary>
    ///     A bare-element predicate (<c>t =&gt; t == "urgent"</c>) is not translated yet;
    ///     <c>Contains</c> is the spelling for an element equality test. Refused rather than
    ///     mis-translated.
    /// </summary>
    [Fact]
    public void a_bare_element_comparison_is_refused()
    {
        Should.Throw<BadLinqExpressionException>(() => SqlFor(x => x.Tags.Any(t => t == "urgent")));
    }

    [Fact]
    public void contains_comparing_against_another_document_member_is_refused()
    {
        var ex = Should.Throw<BadLinqExpressionException>(() => SqlFor(x => x.Tags.Contains(x.Name)));

        ex.Message.ShouldContain("not another document member");
    }

    [Fact]
    public void contains_over_complex_elements_is_refused_toward_any()
    {
        var oslo = new Stop { Port = "Oslo" };

        var ex = Should.Throw<BadLinqExpressionException>(() => SqlFor(x => x.Stops.Contains(oslo)));

        ex.Message.ShouldContain("Any(c => ");
    }

    /// <summary>
    ///     <c>IsEmpty()</c> keeps its own translation — the null-inclusive json_array_length form —
    ///     untouched by any of this.
    /// </summary>
    [Fact]
    public async Task is_empty_still_answers_the_null_inclusive_question()
    {
        (await NamesMatchingAsync(x => x.Tags.IsEmpty())).ShouldBe(["Fram"]);
    }
}
