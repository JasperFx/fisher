using System.Linq.Expressions;
using Fisher.Linq;
using Fisher.Linq.Members;
using Fisher.Linq.Parsing;
using JasperFx;

namespace Fisher.Tests.Linq;

/// <summary>
///     Predicate translation, asserted both as generated SQL and by running it against documents the
///     store actually wrote.
/// </summary>
/// <remarks>
///     The executed half matters more than the text half here. SQLite's <c>LIKE</c> is
///     case-insensitive for ASCII while <c>=</c> is case-sensitive, so a translator that reached for
///     <c>LIKE</c> would produce a query surface that disagrees with itself — and only an executed test
///     over mixed-case data shows it.
/// </remarks>
public class where_clause_parsing : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("where");
    private DocumentStore _store = null!;
    private readonly Guid _frodoId = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Explorer>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = _store.LightweightSession();
        session.Store(new Explorer
        {
            Id = _frodoId, Name = "Frodo", Age = 33, Active = true,
            Grade = Grade.HighDistinction, Nickname = "Ring-bearer",
            Home = new Address { City = "Bag End" }
        });
        session.Store(new Explorer
        {
            Id = Guid.NewGuid(), Name = "frodo", Age = 51, Active = false,
            Grade = Grade.Pass, Nickname = "impostor",
            Home = new Address { City = "Bree" }
        });
        session.Store(new Explorer
        {
            Id = Guid.NewGuid(), Name = "Samwise", Age = 38, Active = true,
            Grade = Grade.Pass, Nickname = "Sam",
            Home = new Address { City = "Bag End" }
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private string SqlFor(Expression<Func<Explorer, bool>> predicate)
    {
        var factory = new MemberFactory(_store.Options, _store.Options.Schema.For<Explorer>());
        var builder = new Weasel.Sqlite.CommandBuilder();
        new WhereClauseParser(factory).Parse(predicate.Body).Apply(builder);
        return builder.Compile().CommandText;
    }

    /// <summary>
    ///     Runs the translated predicate and returns the matching names, so an assertion can be about
    ///     rows rather than about SQL text.
    /// </summary>
    private async Task<List<string>> NamesMatchingAsync(Expression<Func<Explorer, bool>> predicate)
    {
        var mapping = _store.Options.Schema.For<Explorer>();
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
    public async Task equality_on_a_string_member()
    {
        SqlFor(x => x.Name == "Frodo").ShouldBe("json_extract(data, '$.name') = @p0");
        (await NamesMatchingAsync(x => x.Name == "Frodo")).ShouldBe(["Frodo"]);
    }

    /// <summary>
    ///     The reason the string parsers use <c>instr</c> rather than <c>LIKE</c>: equality is
    ///     case-sensitive, so <c>Contains</c> must be too, or the same query surface contradicts itself.
    /// </summary>
    [Fact]
    public async Task string_equality_is_case_sensitive()
    {
        (await NamesMatchingAsync(x => x.Name == "frodo")).ShouldBe(["frodo"]);
    }

    [Fact]
    public async Task contains_is_case_sensitive_like_the_clr_method()
    {
        SqlFor(x => x.Name.Contains("rod")).ShouldBe("instr(json_extract(data, '$.name'), @p0) > 0");

        (await NamesMatchingAsync(x => x.Name.Contains("rod"))).ShouldBe(["Frodo", "frodo"]);
        (await NamesMatchingAsync(x => x.Name.Contains("Rod"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task starts_with_is_case_sensitive()
    {
        SqlFor(x => x.Name.StartsWith("Fro")).ShouldBe("instr(json_extract(data, '$.name'), @p0) = 1");

        (await NamesMatchingAsync(x => x.Name.StartsWith("Fro"))).ShouldBe(["Frodo"]);
        (await NamesMatchingAsync(x => x.Name.StartsWith("fro"))).ShouldBe(["frodo"]);
    }

    [Fact]
    public async Task ends_with_binds_one_parameter_using_the_known_needle_length()
    {
        SqlFor(x => x.Name.EndsWith("do")).ShouldBe("substr(json_extract(data, '$.name'), -2) = @p0");

        (await NamesMatchingAsync(x => x.Name.EndsWith("do"))).ShouldBe(["Frodo", "frodo"]);
    }

    /// <summary>
    ///     .NET says <c>EndsWith("")</c> is true; <c>substr(x, 0)</c> returns the whole string, so the
    ///     generated form would say false without the special case.
    /// </summary>
    [Fact]
    public async Task ends_with_an_empty_string_matches_everything()
    {
        (await NamesMatchingAsync(x => x.Name.EndsWith(""))).Count.ShouldBe(3);
    }

    [Fact]
    public async Task an_explicit_ignore_case_comparison_folds_both_sides()
    {
        SqlFor(x => x.Name.StartsWith("FRO", StringComparison.OrdinalIgnoreCase))
            .ShouldBe("instr(lower(json_extract(data, '$.name')), @p0) = 1");

        (await NamesMatchingAsync(x => x.Name.StartsWith("FRO", StringComparison.OrdinalIgnoreCase)))
            .ShouldBe(["Frodo", "frodo"]);
    }

    [Fact]
    public async Task to_lower_transforms_the_locator()
    {
        SqlFor(x => x.Name.ToLower() == "frodo").ShouldBe("lower(json_extract(data, '$.name')) = @p0");

        (await NamesMatchingAsync(x => x.Name.ToLower() == "frodo")).ShouldBe(["Frodo", "frodo"]);
    }

    [Fact]
    public async Task numeric_comparison_needs_no_cast()
    {
        SqlFor(x => x.Age > 35).ShouldBe("json_extract(data, '$.age') > @p0");

        (await NamesMatchingAsync(x => x.Age > 35)).ShouldBe(["Samwise", "frodo"]);
    }

    /// <summary>
    ///     A reversed comparison flips the operator rather than the operands.
    /// </summary>
    [Fact]
    public async Task a_reversed_comparison_reverses_the_operator()
    {
        SqlFor(x => 35 < x.Age).ShouldBe("json_extract(data, '$.age') > @p0");

        (await NamesMatchingAsync(x => 35 < x.Age)).ShouldBe(["Samwise", "frodo"]);
    }

    [Fact]
    public async Task a_boolean_member_compares_against_the_integer_stored()
    {
        SqlFor(x => x.Active).ShouldBe("json_extract(data, '$.active') = @p0");

        (await NamesMatchingAsync(x => x.Active)).ShouldBe(["Frodo", "Samwise"]);
        (await NamesMatchingAsync(x => !x.Active)).ShouldBe(["frodo"]);
    }

    [Fact]
    public async Task compound_predicates_parenthesise_and_bind_correctly()
    {
        SqlFor(x => x.Age > 35 && x.Active)
            .ShouldBe("(json_extract(data, '$.age') > @p0 and json_extract(data, '$.active') = @p1)");

        (await NamesMatchingAsync(x => x.Age > 35 && x.Active)).ShouldBe(["Samwise"]);
        (await NamesMatchingAsync(x => x.Name == "Frodo" || x.Name == "Samwise")).ShouldBe(["Frodo", "Samwise"]);
    }

    /// <summary>
    ///     Precedence is the whole reason the fragments parenthesise: without it the <c>or</c> would
    ///     swallow the <c>and</c> and pull in an extra row.
    /// </summary>
    [Fact]
    public async Task or_nested_under_and_keeps_its_grouping()
    {
        var names = await NamesMatchingAsync(x => x.Active && (x.Name == "Frodo" || x.Name == "frodo"));

        names.ShouldBe(["Frodo"]);
    }

    [Fact]
    public async Task a_nested_member_resolves_through_the_json_path()
    {
        (await NamesMatchingAsync(x => x.Home.City == "Bag End")).ShouldBe(["Frodo", "Samwise"]);
    }

    [Fact]
    public async Task a_closure_variable_is_evaluated_when_the_query_is_built()
    {
        var wanted = "Samwise";

        (await NamesMatchingAsync(x => x.Name == wanted)).ShouldBe(["Samwise"]);
    }

    [Fact]
    public async Task a_collection_contains_becomes_an_in_clause()
    {
        var wanted = new[] { "Frodo", "Samwise" };

        SqlFor(x => wanted.Contains(x.Name)).ShouldBe("json_extract(data, '$.name') in (@p0, @p1)");
        (await NamesMatchingAsync(x => wanted.Contains(x.Name))).ShouldBe(["Frodo", "Samwise"]);
    }

    /// <summary>
    ///     Guids in the set go through the member's conversion, which is what keeps them matching the
    ///     lowercase canonical text on disk.
    /// </summary>
    [Fact]
    public async Task a_contains_over_ids_converts_each_element()
    {
        var ids = new[] { _frodoId };

        (await NamesMatchingAsync(x => ids.Contains(x.Id))).ShouldBe(["Frodo"]);
    }

    [Fact]
    public void an_empty_collection_contains_matches_nothing_rather_than_failing()
    {
        var none = Array.Empty<string>();

        SqlFor(x => none.Contains(x.Name)).ShouldBe("1=0");
    }

    [Fact]
    public async Task string_length_uses_sqlites_length_function()
    {
        SqlFor(x => x.Name.Length == 5).ShouldBe("length(json_extract(data, '$.name')) = @p0");

        (await NamesMatchingAsync(x => x.Name.Length == 5)).ShouldBe(["Frodo", "frodo"]);
    }

    [Fact]
    public async Task an_enum_compares_against_its_stored_integer()
    {
        (await NamesMatchingAsync(x => x.Grade == Grade.HighDistinction)).ShouldBe(["Frodo"]);
    }

    /// <summary>
    ///     Equality on a date is supported because the literal is rendered by the same serializer that
    ///     wrote the document.
    /// </summary>
    [Fact]
    public void equality_on_a_date_is_allowed()
    {
        Should.NotThrow(() => SqlFor(x => x.JoinedAt == DateTimeOffset.UtcNow));
    }

    /// <summary>
    ///     Ordering is not, and refusing is the point — the stored text does not sort by instant, so any
    ///     emitted range predicate would return plausible but wrong rows.
    /// </summary>
    [Theory]
    [InlineData(">")]
    [InlineData("<")]
    public void range_comparison_on_a_date_is_refused(string op)
    {
        var when = DateTimeOffset.UtcNow;

        var ex = Should.Throw<BadLinqExpressionException>(() =>
            op == ">" ? SqlFor(x => x.JoinedAt > when) : SqlFor(x => x.JoinedAt < when));

        ex.Message.ShouldContain("order-preserving");
    }

    [Fact]
    public void an_untranslatable_method_names_itself()
    {
        var ex = Should.Throw<BadLinqExpressionException>(() => SqlFor(x => x.Name.Normalize() == "x"));

        ex.Message.ShouldContain("Normalize");
    }
}
