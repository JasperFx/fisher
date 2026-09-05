using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json.Serialization;
using Fisher.Linq;
using Fisher.Linq.Members;
using Fisher.Linq.Parsing;
using JasperFx;
using Weasel.Core;

namespace Fisher.Tests.Linq;

/// <summary>
///     Regression tests from the LINQ-provider SQL-injection audit (the marten#4911 / marten#4954
///     class): every runtime value reaching generated SQL must arrive as a bound parameter or be
///     escaped, never interpolated as text.
/// </summary>
/// <remarks>
///     <para>
///         The audited hole was the modulo translation, which rendered both of its runtime operands
///         with an interpolated <c>ToString()</c> — no parameter, no type guard, current-culture
///         formatting. The expression tree's typing confines the ordinary API to numeric operands,
///         so the practical exposure was malformed SQL under a comma-decimal culture and a breakout
///         via a user-defined <c>operator %</c>'s <c>ToString()</c>; both are closed by binding, and
///         the SQL-text assertions here are what hold it closed.
///     </para>
///     <para>
///         The member-locator half mirrors marten#4911's <c>DictionaryItemMember</c> fix: a value
///         inlined into the single-quoted JSON-path literal must have its quotes escaped. Fisher's
///         only path source today is <c>[JsonPropertyName]</c> — compile-time configuration — so
///         this is defence in depth rather than a live exploit, pinned so a future runtime-supplied
///         path segment cannot inherit a breakout.
///     </para>
/// </remarks>
public class sql_injection_hardening : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("sqli");
    private DocumentStore _store = null!;

    public class Specimen
    {
        public Guid Id { get; set; }
        public int Number { get; set; }
        public double Weight { get; set; }

        // A quote in the stored key is exactly the character that would close the JSON-path string
        // literal the locator embeds it in.
        [JsonPropertyName("o'brien")]
        public string Quoted { get; set; } = "";
    }

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Specimen>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = _store.LightweightSession();
        session.Store(new Specimen { Id = Guid.NewGuid(), Number = 3, Weight = 1.25, Quoted = "match" });
        session.Store(new Specimen { Id = Guid.NewGuid(), Number = 4, Weight = 2.5, Quoted = "other" });
        session.Store(new Specimen { Id = Guid.NewGuid(), Number = 5, Weight = 4.5, Quoted = "other" });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private Microsoft.Data.Sqlite.SqliteCommand CommandFor(Expression<Func<Specimen, bool>> predicate)
    {
        var factory = new MemberFactory(_store.Options, _store.Options.Schema.For<Specimen>().Mapping);
        var builder = new Weasel.Sqlite.CommandBuilder();
        new WhereClauseParser(factory).Parse(predicate.Body).Apply(builder);
        return (Microsoft.Data.Sqlite.SqliteCommand)builder.Compile();
    }

    [Fact]
    public void modulo_operands_are_bound_as_parameters_not_interpolated()
    {
        var divisor = 2;
        var remainder = 0;

        var command = CommandFor(x => x.Number % divisor == remainder);

        command.CommandText.ShouldBe("(json_extract(data, '$.number') % @p0) = @p1");
        command.Parameters.Count.ShouldBe(2);
        command.Parameters["p0"].Value.ShouldBe(2);
        command.Parameters["p1"].Value.ShouldBe(0);
    }

    [Fact]
    public void a_reversed_modulo_comparison_is_bound_the_same_way()
    {
        var command = CommandFor(x => 0 < x.Number % 3);

        command.CommandText.ShouldBe("(json_extract(data, '$.number') % @p0) > @p1");
        command.Parameters["p0"].Value.ShouldBe(3);
        command.Parameters["p1"].Value.ShouldBe(0);
    }

    [Fact]
    public async Task a_modulo_predicate_still_answers_correctly()
    {
        await using var session = _store.LightweightSession();

        var even = await session.Query<Specimen>()
            .Where(x => x.Number % 2 == 0)
            .ToListAsync(TestContext.Current.CancellationToken);

        even.ShouldHaveSingleItem().Number.ShouldBe(4);
    }

    /// <summary>
    ///     The old interpolation rendered a fractional operand with the current culture, so under a
    ///     comma-decimal culture <c>x.Weight % 1.5</c> became <c>(… % 1,5) = …</c> — SQL whose shape
    ///     depends on the server's locale. A bound parameter cannot.
    /// </summary>
    [Fact]
    public async Task a_fractional_modulo_operand_survives_a_comma_decimal_culture()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

        try
        {
            var command = CommandFor(x => x.Weight % 1.5 == 0);

            command.CommandText.ShouldBe("(json_extract(data, '$.weight') % @p0) = @p1");
            command.Parameters["p0"].Value.ShouldBe(1.5);

            // SQLite's % casts both operands to INTEGER, so every weight satisfies "% 1.5 == 0"
            // (n % 1 is always 0). The assertion here is that the statement is *valid* whatever
            // the culture — the old interpolation rendered "(… % 1,5) = 0", which SQLite refuses —
            // not a claim about float-modulo semantics.
            await using var session = _store.LightweightSession();
            var matches = await session.Query<Specimen>()
                .Where(x => x.Weight % 1.5 == 0)
                .ToListAsync(TestContext.Current.CancellationToken);

            matches.Count.ShouldBe(3);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    ///     The marten#4911 shape: a value inlined into the locator's single-quoted JSON-path literal
    ///     is escaped, so a quote in an explicit <c>[JsonPropertyName]</c> stays part of the path
    ///     rather than closing the string.
    /// </summary>
    [Fact]
    public void a_quoted_json_property_name_is_escaped_in_the_locator()
    {
        var factory = new MemberFactory(_store.Options, _store.Options.Schema.For<Specimen>().Mapping);
        var member = factory.ResolveMember(
            (MemberExpression)((Expression<Func<Specimen, string>>)(x => x.Quoted)).Body);

        member.RawLocator.ShouldBe("json_extract(data, '$.o''brien')");
    }

    [Fact]
    public async Task a_quoted_json_property_name_still_queries_end_to_end()
    {
        await using var session = _store.LightweightSession();

        var matches = await session.Query<Specimen>()
            .Where(x => x.Quoted == "match")
            .ToListAsync(TestContext.Current.CancellationToken);

        matches.ShouldHaveSingleItem().Number.ShouldBe(3);
    }
}
