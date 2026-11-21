using System.Linq.Expressions;
using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

public class KeyedDoc
{
    public Guid Id { get; set; }
    public int Number { get; set; }
    public int? Maybe { get; set; }
    public long Count { get; set; }
    public short Small { get; set; }
    public double Amount { get; set; }
    public decimal Price { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public bool Flag { get; set; }
    public KeyedInner Inner { get; set; } = new();
}

public class KeyedInner
{
    public int Level { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>
///     Ordering keys computed from the document rather than naming a member — fisher#666, the port of
///     marten#5957. LINQ to Objects over the same documents is the oracle throughout.
/// </summary>
/// <remarks>
///     <para>
///         Before this every computed key was refused by name, so nothing came back wrong. Marten's bug was the
///         other failure: <c>OrderBy(x =&gt; -x.Number)</c> sorted by <c>x.Number</c>. Every key here is compared,
///         in order, with LINQ to Objects, ascending and descending, with <c>x.Number</c> breaking ties.
///     </para>
///     <para>
///         The data carries the <c>int</c> extremes, negative numbers and a null on purpose: those are where
///         SQLite's 64-bit arithmetic and unchecked .NET's 32-bit arithmetic part company. Strings are lower case
///         ASCII and compared ordinally, which is how SQLite compares them.
///     </para>
/// </remarks>
public class computed_ordering_keys : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("computed-ordering-keys");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string[] Words = ["pear", "fig", "banana", "kiwi", "apple", "plum", "cherry", "date", "lime"];

    private static readonly KeyedDoc[] Docs = Enumerable.Range(1, 9).Select(i => new KeyedDoc
    {
        Id = Guid.NewGuid(),
        Number = i switch { 4 => int.MaxValue, 7 => int.MinValue, 8 => -8, _ => i },
        Maybe = i % 3 == 0 ? null : 10 - i,
        Count = (i * 7) % 10 - 4,
        Small = (short)(i * 3 % 7 - 2),
        Amount = (i % 4) * 1.25 - i / 10.0,
        Price = i * 1.5m,
        Name = Words[i - 1],
        Code = i % 2 == 0 ? "b" : "a",
        Flag = i % 3 == 0,
        Inner = new KeyedInner { Level = (i * 5) % 7 - 3, Label = new[] { "x", "yy", "zzz" }[i % 3] }
    }).ToArray();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<KeyedDoc>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        // Out of Number order, so an ordering that goes missing shows up.
        await using var session = _store.LightweightSession();
        foreach (var doc in Docs.OrderBy(x => x.Name, StringComparer.Ordinal)) session.Store(doc);
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private IQuerySession Session() => _store.QuerySession();

    private static Expression<Func<KeyedDoc, T>> Key<T>(Expression<Func<KeyedDoc, T>> key) => key;

    /// <summary>
    ///     <c>OrderBy(key)</c> or <c>OrderByDescending(key)</c>, then <c>ThenBy(x =&gt; x.Number)</c>, over either
    ///     provider. Built as an expression because the keys have different types.
    /// </summary>
    private static IQueryable<KeyedDoc> Order(IQueryable<KeyedDoc> source, LambdaExpression key, bool descending)
    {
        var ordered = (IOrderedQueryable<KeyedDoc>)source.Provider.CreateQuery<KeyedDoc>(Expression.Call(
            typeof(Queryable), descending ? nameof(Queryable.OrderByDescending) : nameof(Queryable.OrderBy),
            [typeof(KeyedDoc), key.ReturnType], source.Expression, Expression.Quote(key)));

        return ordered.ThenBy(x => x.Number);
    }

    /// <summary>The numeric keys, as they would be written in a query.</summary>
    public static TheoryData<string> NumericKeys =>
    [
        "negate", "negate_nested", "negate_long", "negate_short", "negate_nullable", "negate_double", "modulo",
        "modulo_by_local", "integer_division", "negated_division", "add_members", "subtract_members",
        "multiply_by_negative", "add_wraps_at_the_maximum", "multiply_wraps", "modulo_of_a_sum",
        "negation_of_a_sum", "widening_cast", "widened_short_arithmetic", "nullable_plus_one", "string_length",
        "length_of_upper"
    ];

    private static LambdaExpression NumericKey(string name)
    {
        var divisor = 4;

        return name switch
        {
            "negate" => Key(x => -x.Number),
            "negate_nested" => Key(x => -x.Inner.Level),
            "negate_long" => Key(x => -x.Count),
            "negate_short" => Key(x => -x.Small),
            "negate_nullable" => Key(x => -x.Maybe),
            "negate_double" => Key(x => -x.Amount),
            "modulo" => Key(x => x.Number % 3),
            "modulo_by_local" => Key(x => x.Number % divisor),
            "integer_division" => Key(x => x.Number / 3),
            "negated_division" => Key(x => -(x.Inner.Level / 2)),
            "add_members" => Key(x => x.Number + x.Inner.Level),
            "subtract_members" => Key(x => x.Inner.Level - x.Number),
            "multiply_by_negative" => Key(x => x.Number * -2),
            "add_wraps_at_the_maximum" => Key(x => x.Number + 1),
            "multiply_wraps" => Key(x => x.Number * 3),
            "modulo_of_a_sum" => Key(x => (x.Number + x.Inner.Level) % 3),
            "negation_of_a_sum" => Key(x => -(x.Inner.Level + x.Small)),
            "widening_cast" => Key(x => (long)x.Number),
            "widened_short_arithmetic" => Key(x => x.Small * x.Inner.Level),
            "nullable_plus_one" => Key(x => x.Maybe + 1),
            "string_length" => Key(x => x.Name.Length),
            "length_of_upper" => Key(x => x.Name.ToUpper().Length),
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
    }

    [Theory]
    [MemberData(nameof(NumericKeys))]
    public async Task numeric_keys_order_as_linq_to_objects_does(string name)
    {
        foreach (var descending in new[] { false, true })
        {
            var key = NumericKey(name);
            var expected = Order(Docs.AsQueryable(), key, descending).Select(x => x.Number).ToList();

            await using var session = Session();
            var query = Order(session.Query<KeyedDoc>(), key, descending).Select(x => x.Number);

            (await query.ToListAsync(Token)).ShouldBe(expected, $"{name} {(descending ? "desc" : "asc")}: "
                                                                + session.ToSql(query));
        }
    }

    /// <summary>The text keys, compared ordinally as SQLite compares them.</summary>
    public static TheoryData<string> TextKeys =>
    [
        "to_upper", "to_lower", "to_upper_invariant", "nested_to_upper", "concatenation", "concatenation_with_a_literal",
        "concatenation_with_an_int", "upper_of_a_concatenation", "int_to_string", "nullable_int_to_string"
    ];

    private static Expression<Func<KeyedDoc, string>> TextKey(string name) => name switch
    {
        "to_upper" => x => x.Name.ToUpper(),
        "to_lower" => x => x.Name.ToLower(),
        "to_upper_invariant" => x => x.Code.ToUpperInvariant(),
        "nested_to_upper" => x => x.Inner.Label.ToUpper(),
        "concatenation" => x => x.Code + x.Name,
        "concatenation_with_a_literal" => x => x.Inner.Label + "-" + x.Code,
        "concatenation_with_an_int" => x => x.Code + x.Inner.Level,
        "upper_of_a_concatenation" => x => (x.Inner.Label + x.Code).ToUpper(),
        "int_to_string" => x => x.Inner.Level.ToString(),
        "nullable_int_to_string" => x => x.Maybe.ToString()!,
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    [Theory]
    [MemberData(nameof(TextKeys))]
    public async Task text_keys_order_as_linq_to_objects_does(string name)
    {
        foreach (var descending in new[] { false, true })
        {
            var key = TextKey(name);
            var compiled = key.Compile();

            var ordered = descending
                ? Docs.OrderByDescending(compiled, StringComparer.Ordinal)
                : Docs.OrderBy(compiled, StringComparer.Ordinal);
            var expected = ordered.ThenBy(x => x.Number).Select(x => x.Number).ToList();

            await using var session = Session();
            var query = (descending ? session.Query<KeyedDoc>().OrderByDescending(key) : session.Query<KeyedDoc>().OrderBy(key))
                .ThenBy(x => x.Number).Select(x => x.Number);

            (await query.ToListAsync(Token)).ShouldBe(expected, $"{name} {(descending ? "desc" : "asc")}: "
                                                                + session.ToSql(query));
        }
    }

    [Fact]
    public async Task negation_is_not_ordered_by_the_member_underneath()
    {
        await using var session = Session();

        #region sample_computed_ordering_key
        var numbers = await session.Query<KeyedDoc>()
            .OrderBy(x => -x.Number)
            .Select(x => x.Number)
            .ToListAsync();
        #endregion

        numbers.ShouldBe(Docs.Select(x => x.Number).OrderBy(x => -x).ToList());
        session.ToSql(session.Query<KeyedDoc>().OrderBy(x => -x.Number)).ShouldContain("-2147483648");
    }

    [Fact]
    public async Task then_by_computed_keys()
    {
        await using var session = Session();

        var expected = Docs.OrderBy(x => x.Code).ThenByDescending(x => x.Number % 4).ThenBy(x => -x.Inner.Level)
            .ThenBy(x => x.Number).Select(x => x.Number).ToList();

        (await session.Query<KeyedDoc>().OrderBy(x => x.Code).ThenByDescending(x => x.Number % 4)
                .ThenBy(x => -x.Inner.Level).ThenBy(x => x.Number).Select(x => x.Number).ToListAsync(Token))
            .ShouldBe(expected);
    }

    [Fact]
    public async Task paging_after_a_computed_key_pages_the_ordered_rows()
    {
        await using var session = Session();

        var expected = Docs.OrderBy(x => x.Number % 3).ThenByDescending(x => x.Number).Skip(2).Take(4)
            .Select(x => x.Number).ToList();

        (await session.Query<KeyedDoc>().OrderBy(x => x.Number % 3).ThenByDescending(x => x.Number)
                .Skip(2).Take(4).Select(x => x.Number).ToListAsync(Token))
            .ShouldBe(expected);
    }

    [Fact]
    public async Task first_and_last_after_a_computed_key()
    {
        await using var session = Session();

        var ordered = Docs.OrderBy(x => x.Inner.Level - x.Number).ThenBy(x => x.Number).ToList();

        (await session.Query<KeyedDoc>().OrderBy(x => x.Inner.Level - x.Number).ThenBy(x => x.Number)
            .FirstAsync(Token)).Number.ShouldBe(ordered.First().Number);

        (await session.Query<KeyedDoc>().OrderBy(x => x.Inner.Level - x.Number).ThenBy(x => x.Number)
            .LastAsync(Token)).Number.ShouldBe(ordered.Last().Number);
    }

    [Fact]
    public async Task a_where_and_a_computed_key()
    {
        await using var session = Session();

        var expected = Docs.Where(x => x.Number > 2).OrderByDescending(x => x.Name.ToUpper(), StringComparer.Ordinal)
            .Select(x => x.Number).ToList();

        (await session.Query<KeyedDoc>().Where(x => x.Number > 2).OrderByDescending(x => x.Name.ToUpper())
                .Select(x => x.Number).ToListAsync(Token))
            .ShouldBe(expected);
    }

    /// <summary>
    ///     A second <c>OrderBy</c> is the primary key, and the first only breaks its ties — fisher#588's rule,
    ///     held for a computed key.
    /// </summary>
    [Fact]
    public async Task a_second_order_by_with_a_computed_key()
    {
        await using var session = Session();

        var expected = Docs.OrderBy(x => x.Number).OrderBy(x => x.Number % 3).Select(x => x.Number).ToList();

        (await session.Query<KeyedDoc>().OrderBy(x => x.Number).OrderBy(x => x.Number % 3).Select(x => x.Number)
            .ToListAsync(Token)).ShouldBe(expected);
    }

    /// <summary>A computed key after <c>Take</c> re-sorts the page, as in LINQ (fisher#589).</summary>
    [Fact]
    public async Task a_computed_key_after_take_orders_the_page()
    {
        await using var session = Session();

        var expected = Docs.OrderBy(x => x.Name, StringComparer.Ordinal).Take(5).OrderBy(x => -x.Inner.Level)
            .ThenBy(x => x.Number).Select(x => x.Number).ToList();

        (await session.Query<KeyedDoc>().OrderBy(x => x.Name).Take(5).OrderBy(x => -x.Inner.Level)
                .ThenBy(x => x.Number).Select(x => x.Number).ToListAsync(Token))
            .ShouldBe(expected);
    }

    [Fact]
    public async Task counting_under_a_computed_key()
    {
        await using var session = Session();

        (await session.Query<KeyedDoc>().OrderBy(x => -x.Number).CountAsync(Token)).ShouldBe(Docs.Length);
    }

    /// <summary>
    ///     A cursor has to carry the key's value, and a computed key has no member to type it by — the same
    ///     refusal a relevance rank gets.
    /// </summary>
    [Fact]
    public async Task keyset_paging_refuses_a_computed_key()
    {
        await using var session = Session();

        await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<KeyedDoc>().OrderBy(x => -x.Number).ThenBy(x => x.Id).ToCursorPageAsync(3, null, Token));
    }

    public static TheoryData<string> RefusedKeys =>
    [
        "decimal_division", "decimal_arithmetic", "floating_modulo", "double_arithmetic", "long_arithmetic",
        "checked_arithmetic", "narrowing_cast", "narrowing_cast_of_a_long", "decimal_to_int", "conditional",
        "math_call", "substring", "not", "double_to_string", "division_by_a_member", "division_by_zero",
        "coalesce"
    ];

    private static Func<IQueryable<KeyedDoc>, IQueryable<KeyedDoc>> RefusedKey(string name)
    {
        var zero = 0;

        return RefusedKey(name, zero);
    }

    private static Func<IQueryable<KeyedDoc>, IQueryable<KeyedDoc>> RefusedKey(string name, int zero) => name switch
    {
        "decimal_division" => q => q.OrderBy(x => x.Price / 3m),
        "decimal_arithmetic" => q => q.OrderBy(x => x.Price - x.Number * 2m),
        "floating_modulo" => q => q.OrderBy(x => x.Amount % 2.0),
        "double_arithmetic" => q => q.OrderBy(x => x.Amount * -1.5),
        "long_arithmetic" => q => q.OrderBy(x => x.Count - x.Number),
        "checked_arithmetic" => q => q.OrderBy(x => checked(x.Number + 1)),
        "narrowing_cast" => q => q.OrderBy(x => (byte)x.Number),
        "narrowing_cast_of_a_long" => q => q.OrderBy(x => (int)x.Count),
        "decimal_to_int" => q => q.OrderBy(x => x.Number).ThenBy(x => (int)x.Price),
        "conditional" => q => q.OrderBy(x => x.Flag ? 1 : 0),
        "math_call" => q => q.OrderBy(x => Math.Abs(x.Number - 5)),
        "substring" => q => q.OrderBy(x => x.Number).ThenBy(x => x.Name.Substring(1)),
        "not" => q => q.OrderBy(x => !x.Flag),
        "double_to_string" => q => q.OrderBy(x => x.Amount.ToString()),
        "division_by_a_member" => q => q.OrderBy(x => x.Number / x.Inner.Level),
        "division_by_zero" => q => q.OrderBy(x => x.Number % zero),
        "coalesce" => q => q.OrderBy(x => x.Maybe ?? 0),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    [Theory]
    [MemberData(nameof(RefusedKeys))]
    public async Task a_key_without_an_exact_translation_is_refused_by_name(string name)
    {
        await using var session = Session();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            RefusedKey(name)(session.Query<KeyedDoc>()).ToListAsync(Token));

        ex.Message.ShouldContain("Fisher cannot order by");
    }

    /// <summary>A plain member key renders exactly as it did before computed keys were translated.</summary>
    [Fact]
    public async Task plain_member_keys_keep_their_sql()
    {
        await using var session = Session();

        session.ToSql(session.Query<KeyedDoc>().OrderBy(x => x.Number).ThenByDescending(x => x.Inner.Label)
                .ThenBy(x => x.Name.Length))
            .ShouldBe("select id, data from fi_doc_keyeddoc order by json_extract(data, '$.number'), "
                      + "json_extract(data, '$.inner.label') desc, length(json_extract(data, '$.name'))");
    }
}
