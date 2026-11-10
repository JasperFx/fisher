#if NET11_0_OR_GREATER
using JasperFx;
using Fisher.Linq;

namespace Fisher.Tests.Unions;

public record Card(string Last4, DateTimeOffset Expires);

/// <summary>One object case and two primitive cases of different JSON kinds, so STJ can read it back.</summary>
public union Tender(Card, int, string);

public class Till
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Tender Tender { get; set; }
    public Tender? Maybe { get; set; }
}

public record Deposited(decimal Amount);
public record Withdrawn(decimal Amount, int Code);

/// <summary>
///     Two object cases where one's shape contains the other's: <c>amount</c> is in both, and only the
///     absence of <c>code</c> tells a Deposited row from a Withdrawn one.
/// </summary>
public union Movement(Deposited, Withdrawn);

public class Account
{
    public Guid Id { get; set; }
    public Movement Last { get; set; }
}

public record Note(decimal Amount, string? Reason);
public union Ambiguous(Deposited, Note);
public union Width(int, long);

public class Oddity
{
    public Guid Id { get; set; }
    public Ambiguous Thing { get; set; }
    public Width Size { get; set; }
}

/// <summary>
///     fisher#466 — LINQ over a document member typed as a C# 15 union, through Weasel's
///     <c>SqliteUnionSqlDialect</c> (weasel#744).
/// </summary>
/// <remarks>
///     C# refuses a union pattern inside an expression tree, so every query here goes through the
///     union's <c>Value</c>: <c>x.Tender.Value is Card</c>, <c>((Card)x.Tender.Value).Last4</c>,
///     <c>(int)x.Tender.Value</c> and <c>x.Tender.Value == null</c>. <see cref="Account" /> cannot be
///     loaded at all (System.Text.Json has no way to pick between two object cases without a
///     classifier), so its facts read ids, which is also what keeps them about the SQL.
/// </remarks>
public class union_members : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("union_members");
    private DocumentStore _store = null!;

    private static readonly DateTimeOffset Expiry = new(2027, 6, 30, 0, 0, 0, TimeSpan.Zero);

    private readonly Guid _card = Guid.NewGuid();
    private readonly Guid _otherCard = Guid.NewGuid();
    private readonly Guid _cash = Guid.NewGuid();
    private readonly Guid _voucher = Guid.NewGuid();
    private readonly Guid _empty = Guid.NewGuid();

    private readonly Guid _smallDeposit = Guid.NewGuid();
    private readonly Guid _bigDeposit = Guid.NewGuid();
    private readonly Guid _bigWithdrawal = Guid.NewGuid();

    private CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Till>();
            options.Schema.For<Account>();
            options.Schema.For<Oddity>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        session.Store(new Till { Id = _card, Name = "card", Tender = new Card("1234", Expiry), Maybe = new Card("9999", Expiry) });
        session.Store(new Till { Id = _otherCard, Name = "other card", Tender = new Card("5678", Expiry.AddYears(2)) });
        session.Store(new Till { Id = _cash, Name = "cash", Tender = 7 });
        session.Store(new Till { Id = _voucher, Name = "voucher", Tender = "1234" });
        session.Store(new Till { Id = _empty, Name = "empty" });

        session.Store(new Account { Id = _smallDeposit, Last = new Deposited(1m) });
        session.Store(new Account { Id = _bigDeposit, Last = new Deposited(500m) });
        session.Store(new Account { Id = _bigWithdrawal, Last = new Withdrawn(500m, 3) });
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<Guid[]> TillsAsync(System.Linq.Expressions.Expression<Func<Till, bool>> filter)
    {
        await using var session = _store.QuerySession();
        var ids = await session.Query<Till>().Where(filter).Select(x => x.Id).ToListAsync(Token);
        return ids.OrderBy(x => x).ToArray();
    }

    private async Task<Guid[]> AccountsAsync(System.Linq.Expressions.Expression<Func<Account, bool>> filter)
    {
        await using var session = _store.QuerySession();
        var ids = await session.Query<Account>().Where(filter).Select(x => x.Id).ToListAsync(Token);
        return ids.OrderBy(x => x).ToArray();
    }

    private static Guid[] Sorted(params Guid[] ids) => ids.OrderBy(x => x).ToArray();

    [Fact]
    public async Task a_document_with_a_union_member_round_trips()
    {
        await using var session = _store.QuerySession();
        var till = await session.LoadAsync<Till>(_card, Token);

        till!.Tender.Value.ShouldBe(new Card("1234", Expiry));
        (await session.LoadAsync<Till>(_cash, Token))!.Tender.Value.ShouldBe(7);
    }

    [Fact]
    public async Task a_type_test_finds_an_object_case()
        => (await TillsAsync(x => x.Tender.Value is Card)).ShouldBe(Sorted(_card, _otherCard));

    [Fact]
    public async Task a_type_test_finds_a_primitive_case()
    {
        (await TillsAsync(x => x.Tender.Value is int)).ShouldBe([_cash]);
        (await TillsAsync(x => x.Tender.Value is string)).ShouldBe([_voucher]);
    }

    [Fact]
    public async Task a_negated_type_test_keeps_the_other_cases()
        => (await TillsAsync(x => !(x.Tender.Value is Card) && x.Tender.Value != null))
            .ShouldBe(Sorted(_cash, _voucher));

    [Fact]
    public async Task the_default_union_is_a_null_test()
    {
        (await TillsAsync(x => x.Tender.Value == null)).ShouldBe([_empty]);
        (await TillsAsync(x => x.Tender.Value != null)).ShouldBe(Sorted(_card, _otherCard, _cash, _voucher));
    }

    [Fact]
    public async Task a_case_member_is_compared()
        => (await TillsAsync(x => ((Card)x.Tender.Value!).Last4 == "1234")).ShouldBe([_card]);

    [Fact]
    public async Task a_case_member_does_not_match_a_row_of_another_case_holding_the_same_text()
        // The voucher row's whole value is "1234"; only the card's Last4 may match.
        => (await TillsAsync(x => ((Card)x.Tender.Value!).Last4 != "5678")).ShouldBe([_card]);

    [Fact]
    public async Task a_timestamp_inside_a_case_compares_as_an_instant()
        => (await TillsAsync(x => ((Card)x.Tender.Value!).Expires > Expiry.AddYears(1))).ShouldBe([_otherCard]);

    [Fact]
    public async Task a_primitive_case_is_compared_through_its_cast()
    {
        (await TillsAsync(x => (int)x.Tender.Value! > 5)).ShouldBe([_cash]);
        (await TillsAsync(x => (string)x.Tender.Value! == "1234")).ShouldBe([_voucher]);
    }

    [Fact]
    public async Task a_nullable_union_member_is_tested_through_its_value()
        => (await TillsAsync(x => x.Maybe!.Value.Value is Card)).ShouldBe([_card]);

    [Fact]
    public async Task a_case_member_can_be_ordered_and_selected()
    {
        await using var session = _store.QuerySession();

        var last4 = await session.Query<Till>()
            .Where(x => x.Tender.Value is Card)
            .OrderByDescending(x => ((Card)x.Tender.Value!).Last4)
            .Select(x => ((Card)x.Tender.Value!).Last4)
            .ToListAsync(Token);

        last4.ShouldBe(["5678", "1234"]);
    }

    [Fact]
    public async Task overlapping_object_cases_are_told_apart_by_what_one_lacks()
    {
        (await AccountsAsync(x => x.Last.Value is Deposited)).ShouldBe(Sorted(_smallDeposit, _bigDeposit));
        (await AccountsAsync(x => x.Last.Value is Withdrawn)).ShouldBe([_bigWithdrawal]);
    }

    [Fact]
    public async Task a_member_shared_by_two_cases_is_read_only_for_the_cast_case()
        // Withdrawn(500) also stores amount = 500 at the same path. Without the case test in the locator
        // it would come back here.
        => (await AccountsAsync(x => ((Deposited)x.Last.Value!).Amount > 100m)).ShouldBe([_bigDeposit]);

    [Fact]
    public async Task the_sql_carries_the_json_type_test()
    {
        await using var session = _store.QuerySession();
        var sql = session.ToSql(session.Query<Account>().Where(x => x.Last.Value is Deposited));

        sql.ShouldContain("json_type(data, '$.\"last\"') = 'object'");
        sql.ShouldContain("json_type(data, '$.\"last\".\"code\"') is null");
    }

    // ---- refusals ----

    [Fact]
    public async Task a_bare_value_is_refused_by_name()
    {
        await using var session = _store.QuerySession();
        var ex = Should.Throw<BadLinqExpressionException>(() =>
            session.ToSql(session.Query<Till>().Where(x => x.Tender.Value == (object)"1234")));
        ex.Message.ShouldContain("is a union");
    }

    [Fact]
    public async Task the_whole_union_is_refused_as_an_ordering_key()
    {
        await using var session = _store.QuerySession();
        var ex = Should.Throw<BadLinqExpressionException>(() =>
            session.ToSql(session.Query<Till>().OrderBy(x => x.Tender)));
        ex.Message.ShouldContain("union member 'Tender'");
    }

    [Fact]
    public async Task a_type_that_is_not_a_case_is_refused_naming_the_cases()
    {
        await using var session = _store.QuerySession();
        var ex = Should.Throw<BadLinqExpressionException>(() =>
            session.ToSql(session.Query<Till>().Where(x => x.Tender.Value is DateTime)));
        ex.Message.ShouldContain("Card, Int32, String");
    }

    [Fact]
    public async Task an_object_case_compared_whole_is_refused()
    {
        var card = new Card("1234", Expiry);
        await using var session = _store.QuerySession();
        Should.Throw<BadLinqExpressionException>(() =>
            session.ToSql(session.Query<Till>().Where(x => (Card)x.Tender.Value! == card)));
    }

    [Fact]
    public async Task two_primitive_cases_of_one_json_kind_are_refused()
    {
        await using var session = _store.QuerySession();
        var ex = Should.Throw<BadLinqExpressionException>(() =>
            session.ToSql(session.Query<Oddity>().Where(x => x.Size.Value is int)));
        ex.Message.ShouldContain("Int64");
    }

    [Fact]
    public async Task object_cases_differing_only_by_an_omittable_property_are_refused()
    {
        // Note's Reason is omitted when null (WhenWritingNull), so Note(5, null) and Deposited(5) are the
        // same JSON. Refusing beats a test that matches both.
        await using var session = _store.QuerySession();
        var ex = Should.Throw<BadLinqExpressionException>(() =>
            session.ToSql(session.Query<Oddity>().Where(x => x.Thing.Value is Deposited)));
        ex.Message.ShouldContain("Note");
    }

    [Fact]
    public async Task a_type_test_on_anything_but_a_union_value_is_refused()
    {
        await using var session = _store.QuerySession();
        Should.Throw<BadLinqExpressionException>(() =>
            session.ToSql(session.Query<Till>().Where(x => (object)x.Name is string)));
    }
}
#endif
