using Fisher.Linq.SqlGeneration;

namespace Fisher.Tests.Linq;

/// <summary>
///     The SQL the fragment set emits, asserted as text.
/// </summary>
/// <remarks>
///     These fragments are ported from Polecat, and most of them are dialect-neutral enough that the
///     port is mechanical. <see cref="Statement" /> is not: paging is where T-SQL and SQLite disagree
///     most, and a wrong <c>limit</c>/<c>offset</c> is the kind of mistake that returns plausible rows
///     rather than an error. Asserting the generated text is the cheapest way to hold it.
/// </remarks>
public class sql_generation
{
    private static string SqlFor(ISqlFragment fragment)
    {
        var builder = new Weasel.Sqlite.CommandBuilder();
        fragment.Apply(builder);
        return builder.Compile().CommandText;
    }

    private static string SqlFor(Statement statement)
    {
        var builder = new Weasel.Sqlite.CommandBuilder();
        statement.Apply(builder);
        return builder.Compile().CommandText;
    }

    private static Statement ADocumentStatement() => new()
    {
        FromTable = "fi_doc_user",
        SelectColumns = "id, data"
    };

    [Fact]
    public void a_comparison_binds_its_value_as_a_parameter()
    {
        var sql = SqlFor(new ComparisonFilter("json_extract(data,'$.Name')", "=", "Frodo"));

        sql.ShouldBe("json_extract(data,'$.Name') = @p0");
    }

    /// <summary>
    ///     Parentheses around a compound are load-bearing: SQL binds <c>and</c> tighter than <c>or</c>,
    ///     so an unparenthesised <c>or</c> nested under an outer <c>and</c> would match the wrong rows.
    /// </summary>
    [Fact]
    public void a_compound_parenthesises_each_level()
    {
        var left = new ComparisonFilter("a", "=", 1);
        var middle = new ComparisonFilter("b", "=", 2);
        var right = new ComparisonFilter("c", "=", 3);

        var sql = SqlFor(CompoundWhereFragment.And([
            CompoundWhereFragment.Or([left, middle]),
            right
        ]));

        sql.ShouldBe("((a = @p0 or b = @p1) and c = @p2)");
    }

    [Fact]
    public void combining_a_single_fragment_does_not_wrap_it()
    {
        var sql = SqlFor(CompoundWhereFragment.And([new ComparisonFilter("a", "=", 1)]));

        sql.ShouldBe("a = @p0");
    }

    [Fact]
    public void combining_nothing_is_an_error_rather_than_empty_sql()
    {
        Should.Throw<ArgumentException>(() => CompoundWhereFragment.And([]));
    }

    [Fact]
    public void a_function_comparison_wraps_the_locator()
    {
        var sql = SqlFor(new FunctionComparisonFilter("lower", "json_extract(data,'$.Name')", "=", "frodo"));

        sql.ShouldBe("lower(json_extract(data,'$.Name')) = @p0");
    }

    [Fact]
    public void a_negation_wraps_its_inner_fragment()
    {
        var sql = SqlFor(new NotFragment(new ComparisonFilter("a", "=", 1)));

        sql.ShouldBe("not (a = @p0)");
    }

    [Fact]
    public void a_statement_with_no_clauses_is_a_bare_select()
    {
        SqlFor(ADocumentStatement()).ShouldBe("select id, data from fi_doc_user");
    }

    [Fact]
    public void wheres_are_joined_with_and()
    {
        var statement = ADocumentStatement();
        statement.Wheres.Add(new ComparisonFilter("a", "=", 1));
        statement.Wheres.Add(new ComparisonFilter("b", "=", 2));

        SqlFor(statement).ShouldBe("select id, data from fi_doc_user where a = @p0 and b = @p1");
    }

    [Fact]
    public void order_by_renders_direction_only_when_descending()
    {
        var statement = ADocumentStatement();
        statement.OrderBys.Add(("a", false));
        statement.OrderBys.Add(("b", true));

        SqlFor(statement).ShouldBe("select id, data from fi_doc_user order by a, b desc");
    }

    /// <summary>
    ///     T-SQL spells this <c>TOP(n)</c>; SQLite has only the one form.
    /// </summary>
    [Fact]
    public void a_limit_alone_is_a_plain_limit()
    {
        var statement = ADocumentStatement();
        statement.Limit = 5;

        SqlFor(statement).ShouldBe("select id, data from fi_doc_user limit 5");
    }

    [Fact]
    public void a_limit_with_an_offset_renders_both()
    {
        var statement = ADocumentStatement();
        statement.Limit = 5;
        statement.Offset = 10;

        SqlFor(statement).ShouldBe("select id, data from fi_doc_user limit 5 offset 10");
    }

    /// <summary>
    ///     The case the T-SQL shape has no equivalent of. SQLite rejects a bare <c>offset</c>, so an
    ///     unbounded skip has to say <c>limit -1</c> — which SQLite reads as "no limit" — first.
    /// </summary>
    [Fact]
    public void an_offset_without_a_limit_emits_an_unbounded_limit()
    {
        var statement = ADocumentStatement();
        statement.Offset = 10;

        SqlFor(statement).ShouldBe("select id, data from fi_doc_user limit -1 offset 10");
    }

    /// <summary>
    ///     Polecat has to emit <c>ORDER BY (SELECT NULL)</c> here because T-SQL demands an ORDER BY
    ///     before OFFSET. SQLite does not, and inventing one would impose a sort nobody asked for.
    /// </summary>
    [Fact]
    public void paging_without_an_order_by_does_not_invent_one()
    {
        var statement = ADocumentStatement();
        statement.Offset = 10;

        SqlFor(statement).ShouldNotContain("order by");
    }

    [Fact]
    public void the_exists_wrapper_yields_a_single_flag()
    {
        var statement = ADocumentStatement();
        statement.SelectColumns = "1";
        statement.Wheres.Add(new ComparisonFilter("a", "=", 1));
        statement.IsExistsWrapper = true;

        SqlFor(statement).ShouldBe("select exists (select 1 from fi_doc_user where a = @p0)");
    }
}
