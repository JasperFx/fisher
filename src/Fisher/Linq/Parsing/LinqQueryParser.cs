using System.Linq.Expressions;
using Fisher.Linq.Members;
using Fisher.Linq.SqlGeneration;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.Parsing;

/// <summary>
///     Which rows of a soft-deleted document type a query wants.
/// </summary>
internal enum SoftDeleteScope
{
    /// <summary>The default: <c>is_deleted = 0</c>.</summary>
    LiveOnly,

    /// <summary><c>MaybeDeleted()</c> — no filter at all.</summary>
    LiveAndDeleted,

    /// <summary><c>IsDeleted()</c>, <c>DeletedSince()</c>, <c>DeletedBefore()</c>.</summary>
    DeletedOnly
}

/// <summary>
///     Walks a LINQ method chain into a <see cref="Statement" />.
/// </summary>
/// <remarks>
///     <para>
///         A much narrower version of Polecat's parser of the same name, covering the operators Fisher
///         can answer today: <c>Where</c>, the four ordering operators, <c>Take</c> and <c>Skip</c>.
///         Grouping, projection and joins are absent because the SQL for them is absent — see
///         <see cref="Statement" />.
///     </para>
///     <para>
///         The chain arrives outermost-call-first, so it is walked to the source and then applied in
///         reverse. Order matters for <c>ThenBy</c>, which must land after the <c>OrderBy</c> it
///         refines.
///     </para>
/// </remarks>
internal class LinqQueryParser
{
    private readonly IMemberResolver _memberFactory;
    private readonly WhereClauseParser _whereParser;

    public LinqQueryParser(IMemberResolver memberFactory)
    {
        _memberFactory = memberFactory;
        _whereParser = new WhereClauseParser(memberFactory);
    }

    /// <summary>
    ///     The where fragments the chain produced, ANDed together by the statement.
    /// </summary>
    public List<ISqlFragment> Wheres { get; } = [];

    public List<(string Locator, bool Descending)> OrderBys { get; } = [];

    public int? Limit { get; private set; }

    public int? Offset { get; private set; }

    /// <summary>
    ///     Which soft-deleted rows the query asked for. The last such operator in the chain wins, so
    ///     <c>MaybeDeleted().IsDeleted()</c> is the deleted ones — reading left to right, as the chain
    ///     does.
    /// </summary>
    public SoftDeleteScope SoftDeleteScope { get; private set; } = SoftDeleteScope.LiveOnly;

    /// <summary>Set by <c>DeletedSince</c>; a lower bound on <c>deleted_at</c>.</summary>
    public DateTimeOffset? DeletedSince { get; private set; }

    /// <summary>Set by <c>DeletedBefore</c>; an upper bound on <c>deleted_at</c>.</summary>
    public DateTimeOffset? DeletedBefore { get; private set; }

    /// <summary>
    ///     Whether the chain used any soft-delete operator at all — asked so that using one against a
    ///     type that is not soft-deleted can be refused rather than silently ignored.
    /// </summary>
    public bool UsedSoftDeleteOperator { get; private set; }

    /// <summary>
    ///     The <c>Select</c> the chain ended with, or null when the query returns documents.
    /// </summary>
    public SelectProjection? Projection { get; private set; }

    /// <summary>Set by <c>Distinct()</c>.</summary>
    public bool IsDistinct { get; private set; }

    /// <summary>The key <c>DistinctBy</c> deduplicates on, or null.</summary>
    public string? DistinctByLocator { get; private set; }

    public void Parse(Expression expression)
    {
        var calls = new List<MethodCallExpression>();

        var current = expression;
        while (current is MethodCallExpression call)
        {
            calls.Add(call);
            current = call.Arguments.Count > 0 ? call.Arguments[0] : null!;
        }

        // Outermost first on the way down, so apply from the source outward.
        for (var i = calls.Count - 1; i >= 0; i--)
        {
            Apply(calls[i]);
        }
    }

    private void Apply(MethodCallExpression call)
    {
        switch (call.Method.Name)
        {
            case "Where":
                Wheres.Add(_whereParser.Parse(UnwrapLambda(call).Body));
                break;

            case "OrderBy":
                OrderBys.Add((OrderingLocatorFor(call), false));
                break;

            case "OrderByDescending":
                OrderBys.Add((OrderingLocatorFor(call), true));
                break;

            case "ThenBy":
                OrderBys.Add((OrderingLocatorFor(call), false));
                break;

            case "ThenByDescending":
                OrderBys.Add((OrderingLocatorFor(call), true));
                break;

            case "Select":
                ApplySelect(call);
                break;

            case "Distinct":
                IsDistinct = true;
                break;

            case "DistinctBy" when Projection is not null:
                throw new BadLinqExpressionException(
                    "DistinctBy is for deduplicating documents by a member. After a Select, use "
                    + "Distinct().");

            case "DistinctBy":
                DistinctByLocator = LocatorFor(call);
                break;

            case "Take":
                Limit = (int)WhereClauseParser.ExtractValue(call.Arguments[1])!;
                break;

            case "Skip":
                Offset = (int)WhereClauseParser.ExtractValue(call.Arguments[1])!;
                break;

            // Matched on the declaring type as well as the name: these are Fisher's own marker
            // methods, and a caller's extension method that happened to share a name is not one.
            case nameof(SoftDeletes.SoftDeletedExtensions.MaybeDeleted)
                when call.Method.DeclaringType == typeof(SoftDeletes.SoftDeletedExtensions):
                MarkSoftDelete(SoftDeleteScope.LiveAndDeleted);
                break;

            case nameof(SoftDeletes.SoftDeletedExtensions.IsDeleted)
                when call.Method.DeclaringType == typeof(SoftDeletes.SoftDeletedExtensions):
                MarkSoftDelete(SoftDeleteScope.DeletedOnly);
                break;

            case nameof(SoftDeletes.SoftDeletedExtensions.DeletedSince)
                when call.Method.DeclaringType == typeof(SoftDeletes.SoftDeletedExtensions):
                MarkSoftDelete(SoftDeleteScope.DeletedOnly);
                DeletedSince = (DateTimeOffset)WhereClauseParser.ExtractValue(call.Arguments[1])!;
                break;

            case nameof(SoftDeletes.SoftDeletedExtensions.DeletedBefore)
                when call.Method.DeclaringType == typeof(SoftDeletes.SoftDeletedExtensions):
                MarkSoftDelete(SoftDeleteScope.DeletedOnly);
                DeletedBefore = (DateTimeOffset)WhereClauseParser.ExtractValue(call.Arguments[1])!;
                break;

            // Terminal operators are handled by the provider, which knows what shape of result to
            // ask the statement for; they contribute nothing to the WHERE/ORDER BY.
            case "Count":
            case "LongCount":
            case "Any":
            case "First":
            case "FirstOrDefault":
            case "Single":
            case "SingleOrDefault":
                ApplyTerminalPredicate(call);
                break;

            default:
                throw new BadLinqExpressionException(
                    $"Fisher cannot translate '{call.Method.Name}' to SQL yet. Supported operators are "
                    + "Where, Select, Distinct, DistinctBy, OrderBy, OrderByDescending, ThenBy, "
                    + "ThenByDescending, Take and Skip.");
        }
    }

    /// <summary>
    ///     A projection ends the chain as far as document members are concerned, so only one is
    ///     allowed and nothing may follow it that needs to resolve one.
    /// </summary>
    /// <remarks>
    ///     A second <c>Select</c> would have to resolve members of the <em>first</em> projection's
    ///     result, which is not a document and has no locators. Refused by name rather than producing a
    ///     confusing "no such member" from the member factory.
    /// </remarks>
    private void ApplySelect(MethodCallExpression call)
    {
        if (Projection is not null)
        {
            throw new BadLinqExpressionException(
                "Fisher supports one Select per query. A second one would have to project members of "
                + "the first projection's result, which is not a document and has no columns.");
        }

        Projection = SelectProjection.For(UnwrapLambda(call), _memberFactory);
    }

    private void MarkSoftDelete(SoftDeleteScope scope)
    {
        UsedSoftDeleteOperator = true;
        SoftDeleteScope = scope;
    }

    /// <summary>
    ///     <c>First(x =&gt; ...)</c> and friends carry an optional predicate, which is a where clause by
    ///     another name.
    /// </summary>
    private void ApplyTerminalPredicate(MethodCallExpression call)
    {
        if (call.Arguments.Count > 1)
        {
            Wheres.Add(_whereParser.Parse(UnwrapLambda(call).Body));
        }
    }

    /// <summary>
    ///     An ordering key, which after a <c>Select</c> is a key over the <em>projection</em> rather
    ///     than over the document.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only the identity — <c>OrderBy(x =&gt; x)</c> over a single-column projection — is
    ///         supported, because that is the idiom
    ///         <c>Select(x =&gt; x.Species).Distinct().OrderBy(x =&gt; x)</c> and it maps to the one
    ///         column the statement already selects.
    ///     </para>
    ///     <para>
    ///         Ordering by a member of a <em>shaped</em> projection is refused, and deliberately so
    ///         rather than for want of effort: the mapping back from an anonymous type's member to a
    ///         locator only exists while the projection is a plain member-for-member copy, and
    ///         <c>SelectProjection</c>'s whole point is that it does not have to be. Ordering before the
    ///         <c>Select</c> always works and reads no worse.
    ///     </para>
    /// </remarks>
    private string OrderingLocatorFor(MethodCallExpression call)
    {
        if (Projection is null)
        {
            return LocatorFor(call);
        }

        var lambda = UnwrapLambda(call);
        var body = lambda.Body;

        while (body is UnaryExpression { NodeType: ExpressionType.Convert } unary)
        {
            body = unary.Operand;
        }

        if (body == lambda.Parameters[0] && Projection.Locators.Length == 1)
        {
            return Projection.Locators[0];
        }

        throw new BadLinqExpressionException(
            $"Fisher cannot order by '{lambda.Body}' after a Select. Only OrderBy(x => x) over a "
            + "single-value projection is supported; order before the Select to sort by a document "
            + "member.");
    }

    /// <summary>
    ///     Resolves an ordering key, refusing one whose stored form does not sort — the same guard the
    ///     where parser applies to a range comparison, for the same reason.
    /// </summary>
    private string LocatorFor(MethodCallExpression call)
    {
        var body = UnwrapLambda(call).Body;

        while (body is UnaryExpression { NodeType: ExpressionType.Convert } unary)
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression memberExpression)
        {
            throw new BadLinqExpressionException(
                $"'{call.Method.Name}' is only supported over a document member.");
        }

        var member = _memberFactory.ResolveMember(memberExpression);

        if (!member.AllowsRangeComparison)
        {
            throw new BadLinqExpressionException(
                $"Cannot order by the {member.MemberType.Name} member in SQLite: its stored form is not "
                + "order-preserving, so the rows would come back in a plausible but wrong order. For an "
                + "enum, storing it as an integer (StoreOptions.Serializer.EnumStorage) makes ordering "
                + "meaningful.");
        }

        return member.TypedLocator;
    }

    private static LambdaExpression UnwrapLambda(MethodCallExpression call)
    {
        var argument = call.Arguments[^1];

        while (argument is UnaryExpression { NodeType: ExpressionType.Quote } quote)
        {
            argument = quote.Operand;
        }

        return (LambdaExpression)argument;
    }
}
