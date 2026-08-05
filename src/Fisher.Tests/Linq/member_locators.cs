using System.Linq.Expressions;
using System.Text.Json.Serialization;
using Fisher.Linq.Members;
using JasperFx;
using Weasel.Core;

namespace Fisher.Tests.Linq;

public enum Grade
{
    Pass,
    HighDistinction
}

public class Explorer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public bool Active { get; set; }
    public Grade Grade { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public Address Home { get; set; } = new();

    [JsonPropertyName("nick_name")]
    public string Nickname { get; set; } = "";
}

public class Address
{
    public string City { get; set; } = "";
}

/// <summary>
///     What a document member resolves to in SQL, and what a predicate literal has to be converted into
///     to match what the serializer wrote.
/// </summary>
/// <remarks>
///     Most of this is cheap to get subtly wrong and expensive to notice, because every failure mode
///     here returns <em>no rows</em> rather than an error: a JSON path built from the CLR member name
///     instead of the camelCase key, a Guid bound as a BLOB, a boolean compared against the string
///     "true". The last test runs a generated locator against a genuinely stored document, which is the
///     only one of these that would catch a naming-policy mistake end to end.
/// </remarks>
public class member_locators
{
    private static MemberFactory FactoryFor<T>(Action<StoreOptions>? configure = null) where T : notnull
    {
        var options = new StoreOptions { ConnectionString = "Data Source=:memory:" };
        configure?.Invoke(options);
        return new MemberFactory(options, options.Schema.For<T>());
    }

    private static IQueryableMember Resolve<T>(Expression<Func<Explorer, T>> expression,
        Action<StoreOptions>? configure = null)
        => FactoryFor<Explorer>(configure).ResolveMember((MemberExpression)expression.Body);

    /// <summary>
    ///     Fisher serializes with camelCase by default, so the path is <c>$.name</c>. Building it from
    ///     the CLR member name would produce <c>$.Name</c> and match nothing.
    /// </summary>
    [Fact]
    public void a_member_resolves_through_the_serializer_naming_policy()
    {
        Resolve(x => x.Name).RawLocator.ShouldBe("json_extract(data, '$.name')");
    }

    [Fact]
    public void an_explicit_json_property_name_wins_verbatim()
    {
        Resolve(x => x.Nickname).RawLocator.ShouldBe("json_extract(data, '$.nick_name')");
    }

    [Fact]
    public void a_nested_member_builds_a_dotted_path()
    {
        Resolve(x => x.Home.City).RawLocator.ShouldBe("json_extract(data, '$.home.city')");
    }

    /// <summary>
    ///     The identity is a real column, not part of the JSON body.
    /// </summary>
    [Fact]
    public void the_identity_member_resolves_to_the_id_column()
    {
        var member = Resolve(x => x.Id);

        member.RawLocator.ShouldBe("id");
        member.ShouldBeOfType<IdMember>();
    }

    /// <summary>
    ///     json_extract returns a JSON number as INTEGER, so unlike SQL Server's JSON_VALUE there is
    ///     nothing to CAST — the typed and raw locators are the same string.
    /// </summary>
    [Fact]
    public void a_numeric_member_needs_no_cast()
    {
        var member = Resolve(x => x.Age);

        member.TypedLocator.ShouldBe(member.RawLocator);
        member.TypedLocator.ShouldNotContain("cast");
    }

    [Fact]
    public void a_boolean_converts_to_the_integer_sqlite_actually_stores()
    {
        var member = Resolve(x => x.Active);

        member.IsBoolean.ShouldBeTrue();
        member.ConvertValue(true).ShouldBe(1);
        member.ConvertValue(false).ShouldBe(0);
    }

    [Fact]
    public void a_guid_id_converts_to_lowercase_canonical_text()
    {
        var id = Guid.NewGuid();

        Resolve(x => x.Id).ConvertValue(id).ShouldBe(id.ToString());
    }

    [Fact]
    public void an_enum_stored_as_an_integer_converts_to_its_number()
    {
        Resolve(x => x.Grade).ConvertValue(Grade.HighDistinction).ShouldBe(1);
    }

    /// <summary>
    ///     The default serializer wires <c>JsonStringEnumConverter(PropertyNamingPolicy)</c>, so an
    ///     AsString enum is stored camelCased and a literal of <c>"HighDistinction"</c> would miss.
    /// </summary>
    [Fact]
    public void an_enum_stored_as_a_string_is_cased_by_the_naming_policy()
    {
        var member = Resolve(x => x.Grade,
            options => options.ConfigureSerialization(enumStorage: EnumStorage.AsString));

        member.ConvertValue(Grade.HighDistinction).ShouldBe("highDistinction");
    }

    /// <summary>
    ///     Rendered through the serializer rather than a format string, because STJ trims trailing
    ///     fractional zeros and no single format reproduces that.
    /// </summary>
    [Fact]
    public void a_date_converts_to_exactly_what_the_serializer_writes()
    {
        var member = Resolve(x => x.JoinedAt);

        member.ConvertValue(new DateTimeOffset(2026, 8, 4, 12, 34, 56, 789, TimeSpan.Zero))
            .ShouldBe("2026-08-04T12:34:56.789+00:00");
        member.ConvertValue(new DateTimeOffset(2026, 8, 4, 12, 34, 56, 0, TimeSpan.Zero))
            .ShouldBe("2026-08-04T12:34:56+00:00");
    }

    /// <summary>
    ///     Those two values above are the whole argument: the same member can be written with or
    ///     without a fractional part, and the offset is preserved rather than normalised, so ordering
    ///     the stored text does not order the instants. Range comparison is refused rather than
    ///     answered wrongly.
    /// </summary>
    [Fact]
    public void a_date_refuses_range_comparison()
    {
        Resolve(x => x.JoinedAt).AllowsRangeComparison.ShouldBeFalse();
        Resolve(x => x.Age).AllowsRangeComparison.ShouldBeTrue();
        Resolve(x => x.Name).AllowsRangeComparison.ShouldBeTrue();
    }
}

/// <summary>
///     The end-to-end half: a locator this factory generates, run as SQL against a document the store
///     actually wrote.
/// </summary>
public class member_locators_against_stored_documents : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("locators");
    private DocumentStore _store = null!;
    private readonly Guid _id = Guid.NewGuid();

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
            Id = _id,
            Name = "Frodo",
            Age = 33,
            Active = true,
            Grade = Grade.HighDistinction,
            Nickname = "Ring-bearer",
            Home = new Address { City = "Bag End" }
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<long> CountWhereAsync(string predicate, object value)
    {
        var table = _store.Options.Schema.For<Explorer>().QuotedTableName;

        await using var connection = await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"select count(*) from {table} where {predicate}";
        command.Parameters.AddWithValue("@value", value);

        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static IQueryableMember MemberFor<T>(DocumentStore store, Expression<Func<Explorer, T>> expression)
        => new MemberFactory(store.Options, store.Options.Schema.For<Explorer>())
            .ResolveMember((MemberExpression)expression.Body);

    [Theory]
    [InlineData("Name", "Frodo")]
    [InlineData("Nickname", "Ring-bearer")]
    public async Task a_string_member_locator_finds_the_stored_document(string member, string expected)
    {
        var queryable = member == "Name"
            ? MemberFor(_store, x => x.Name)
            : MemberFor(_store, x => x.Nickname);

        var count = await CountWhereAsync($"{queryable.TypedLocator} = @value",
            queryable.ConvertValue(expected)!);

        count.ShouldBe(1);
    }

    [Fact]
    public async Task a_nested_member_locator_finds_the_stored_document()
    {
        var member = MemberFor(_store, x => x.Home.City);

        var count = await CountWhereAsync($"{member.TypedLocator} = @value", member.ConvertValue("Bag End")!);

        count.ShouldBe(1);
    }

    /// <summary>
    ///     The numeric comparison json_extract makes possible without a CAST.
    /// </summary>
    [Fact]
    public async Task a_numeric_member_locator_compares_numerically()
    {
        var member = MemberFor(_store, x => x.Age);

        (await CountWhereAsync($"{member.TypedLocator} > @value", member.ConvertValue(30)!)).ShouldBe(1);
        (await CountWhereAsync($"{member.TypedLocator} > @value", member.ConvertValue(40)!)).ShouldBe(0);
    }

    [Fact]
    public async Task a_boolean_member_locator_finds_the_stored_document()
    {
        var member = MemberFor(_store, x => x.Active);

        (await CountWhereAsync($"{member.TypedLocator} = @value", member.ConvertValue(true)!)).ShouldBe(1);
        (await CountWhereAsync($"{member.TypedLocator} = @value", member.ConvertValue(false)!)).ShouldBe(0);
    }

    /// <summary>
    ///     The id is the column, and the conversion is what keeps it matching the lowercase canonical
    ///     text SqliteGuidIdentification wrote.
    /// </summary>
    [Fact]
    public async Task the_id_locator_finds_the_stored_document()
    {
        var member = MemberFor(_store, x => x.Id);

        (await CountWhereAsync($"{member.TypedLocator} = @value", member.ConvertValue(_id)!)).ShouldBe(1);
        (await CountWhereAsync($"{member.TypedLocator} = @value", member.ConvertValue(Guid.NewGuid())!)).ShouldBe(0);
    }

    [Fact]
    public async Task an_enum_member_locator_finds_the_stored_document()
    {
        var member = MemberFor(_store, x => x.Grade);

        (await CountWhereAsync($"{member.TypedLocator} = @value", member.ConvertValue(Grade.HighDistinction)!))
            .ShouldBe(1);
        (await CountWhereAsync($"{member.TypedLocator} = @value", member.ConvertValue(Grade.Pass)!)).ShouldBe(0);
    }
}
