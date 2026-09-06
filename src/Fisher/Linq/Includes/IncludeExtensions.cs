using System.Linq.Expressions;
using System.Reflection;
using Fisher.Storage;

namespace Fisher.Linq.Includes;

/// <summary>
///     <c>Include()</c> — fetch the documents a query's rows point at, in the same call
///     (<a href="https://github.com/JasperFx/fisher/issues/204">fisher#204</a>).
/// </summary>
/// <remarks>
///     <para>
///         Extension methods on <see cref="IQueryable{T}" /> rather than members of an interface the
///         way Marten's are, for the reason <see cref="QueryableExtensions" /> records: the chain's
///         intermediate operators come from <see cref="System.Linq.Queryable" /> and hand back a plain
///         <see cref="IQueryable{T}" />, so only an extension can be called after a <c>Where</c>. The
///         upshot is that an <c>Include</c> may sit anywhere in the chain and mean the same thing.
///     </para>
///     <para>
///         <b>The plan rides in the expression tree, not on the provider.</b> Fisher caches one
///         <see cref="FisherQueryProvider" /> per session — see <c>FisherSession.Query&lt;T&gt;</c> —
///         where Marten builds one per <c>Query&lt;T&gt;()</c> call, so a list of includes hung off
///         the provider would leak from one query into the next one the session ran. Each
///         <c>Include</c> instead appends an <see cref="IncludeMarker{T}" /> node carrying its plan,
///         which makes the includes a property of the query the caller built and of nothing else.
///     </para>
///     <para>
///         <b>Two directions.</b> The overloads taking only an <c>idSource</c> match it against the
///         included document's <em>identity</em> — the ordinary "this document holds a foreign key"
///         case. The overloads that also take an <c>idMapping</c> match it against a member of the
///         included document instead, which is how you fetch the many documents that point back at
///         each row of the query.
///     </para>
///     <para>
///         Every one of these resolves as a second <c>SELECT</c> once the query's own rows are in
///         hand; <see cref="IncludePlan{TParent,TInclude}" /> explains why that is the right shape for
///         an embedded store and what it costs.
///     </para>
/// </remarks>
public static class IncludeExtensions
{
    private static readonly MethodInfo MarkerMethod = typeof(IncludeExtensions)
        .GetMethod(nameof(IncludeMarker), BindingFlags.NonPublic | BindingFlags.Static)!;

    // ---- identity direction: the parent member holds the included document's id ----

    /// <summary>
    ///     Fetch each related document and hand it to <paramref name="callback" />.
    /// </summary>
    /// <param name="queryable">The query.</param>
    /// <param name="idSource">
    ///     The member holding the related document's identity. A collection member fans out, so
    ///     <c>x =&gt; x.CrewIds</c> includes every crew member of every row.
    /// </param>
    /// <param name="callback">Called once per distinct related document found.</param>
    /// <param name="filter">An optional extra predicate on the related documents.</param>
    public static IQueryable<T> Include<T, TInclude>(this IQueryable<T> queryable,
        Expression<Func<T, object?>> idSource, Action<TInclude> callback,
        Expression<Func<TInclude, bool>>? filter = null)
        where T : notnull where TInclude : notnull
    {
        ArgumentNullException.ThrowIfNull(callback);

        return Attach(queryable,
            new IncludePlan<T, TInclude>(Compile(idSource), null, filter, callback));
    }

    /// <inheritdoc cref="Include{T,TInclude}(IQueryable{T},Expression{Func{T,object}},Action{TInclude},Expression{Func{TInclude,bool}})" />
    /// <param name="list">Each distinct related document is added once.</param>
    public static IQueryable<T> Include<T, TInclude>(this IQueryable<T> queryable,
        Expression<Func<T, object?>> idSource, IList<TInclude> list,
        Expression<Func<TInclude, bool>>? filter = null)
        where T : notnull where TInclude : notnull
    {
        ArgumentNullException.ThrowIfNull(list);

        return Attach(queryable,
            new IncludePlan<T, TInclude>(Compile(idSource), null, filter, list.Add));
    }

    /// <inheritdoc cref="Include{T,TInclude}(IQueryable{T},Expression{Func{T,object}},Action{TInclude},Expression{Func{TInclude,bool}})" />
    /// <param name="dictionary">Keyed by the related document's own identity.</param>
    public static IQueryable<T> Include<T, TKey, TInclude>(this IQueryable<T> queryable,
        Expression<Func<T, object?>> idSource, IDictionary<TKey, TInclude> dictionary,
        Expression<Func<TInclude, bool>>? filter = null)
        where T : notnull where TKey : notnull where TInclude : notnull
    {
        ArgumentNullException.ThrowIfNull(dictionary);

        var identity = IdentityAccessor<TKey, TInclude>();

        return Attach(queryable, new IncludePlan<T, TInclude>(Compile(idSource), null, filter,
            document => dictionary[identity(document)] = document));
    }

    // ---- mapping direction: a member of the included document points back at the parent ----

    /// <summary>
    ///     Fetch the related documents whose <paramref name="idMapping" /> member matches
    ///     <paramref name="idSource" />, and hand each to <paramref name="callback" />.
    /// </summary>
    /// <param name="queryable">The query.</param>
    /// <param name="idSource">The parent member whose value the related documents point at.</param>
    /// <param name="idMapping">
    ///     The member of the related document to match. Unlike <paramref name="idSource" /> this
    ///     becomes SQL, so it must be a member Fisher can resolve to a column.
    /// </param>
    /// <param name="callback">Called once per distinct related document found.</param>
    /// <param name="filter">An optional extra predicate on the related documents.</param>
    public static IQueryable<T> Include<T, TKey, TInclude>(this IQueryable<T> queryable,
        Expression<Func<T, object?>> idSource, Expression<Func<TInclude, TKey>> idMapping,
        Action<TInclude> callback, Expression<Func<TInclude, bool>>? filter = null)
        where T : notnull where TKey : notnull where TInclude : notnull
    {
        ArgumentNullException.ThrowIfNull(idMapping);
        ArgumentNullException.ThrowIfNull(callback);

        return Attach(queryable,
            new IncludePlan<T, TInclude>(Compile(idSource), idMapping, filter, callback));
    }

    /// <inheritdoc cref="Include{T,TKey,TInclude}(IQueryable{T},Expression{Func{T,object}},Expression{Func{TInclude,TKey}},Action{TInclude},Expression{Func{TInclude,bool}})" />
    /// <param name="list">Each distinct related document is added once, flat.</param>
    public static IQueryable<T> Include<T, TKey, TInclude>(this IQueryable<T> queryable,
        Expression<Func<T, object?>> idSource, Expression<Func<TInclude, TKey>> idMapping,
        IList<TInclude> list, Expression<Func<TInclude, bool>>? filter = null)
        where T : notnull where TKey : notnull where TInclude : notnull
    {
        ArgumentNullException.ThrowIfNull(idMapping);
        ArgumentNullException.ThrowIfNull(list);

        return Attach(queryable,
            new IncludePlan<T, TInclude>(Compile(idSource), idMapping, filter, list.Add));
    }

    /// <inheritdoc cref="Include{T,TKey,TInclude}(IQueryable{T},Expression{Func{T,object}},Expression{Func{TInclude,TKey}},Action{TInclude},Expression{Func{TInclude,bool}})" />
    /// <param name="dictionary">
    ///     Keyed by the related document's <paramref name="idMapping" /> value. A second document with
    ///     the same key replaces the first — use the <c>IList</c>-valued overload when a key can have
    ///     more than one.
    /// </param>
    public static IQueryable<T> Include<T, TKey, TInclude>(this IQueryable<T> queryable,
        Expression<Func<T, object?>> idSource, Expression<Func<TInclude, TKey>> idMapping,
        IDictionary<TKey, TInclude> dictionary, Expression<Func<TInclude, bool>>? filter = null)
        where T : notnull where TKey : notnull where TInclude : notnull
    {
        ArgumentNullException.ThrowIfNull(idMapping);
        ArgumentNullException.ThrowIfNull(dictionary);

        var key = idMapping.Compile();

        return Attach(queryable, new IncludePlan<T, TInclude>(Compile(idSource), idMapping, filter,
            document => dictionary[key(document)] = document));
    }

    /// <inheritdoc cref="Include{T,TKey,TInclude}(IQueryable{T},Expression{Func{T,object}},Expression{Func{TInclude,TKey}},Action{TInclude},Expression{Func{TInclude,bool}})" />
    /// <param name="dictionary">
    ///     Grouped by the related document's <paramref name="idMapping" /> value. A key with no
    ///     matching documents is absent rather than present-and-empty.
    /// </param>
    public static IQueryable<T> Include<T, TKey, TInclude>(this IQueryable<T> queryable,
        Expression<Func<T, object?>> idSource, Expression<Func<TInclude, TKey>> idMapping,
        IDictionary<TKey, IList<TInclude>> dictionary,
        Expression<Func<TInclude, bool>>? filter = null)
        where T : notnull where TKey : notnull where TInclude : notnull
    {
        ArgumentNullException.ThrowIfNull(idMapping);
        ArgumentNullException.ThrowIfNull(dictionary);

        var key = idMapping.Compile();

        return Attach(queryable, new IncludePlan<T, TInclude>(Compile(idSource), idMapping, filter,
            document =>
            {
                if (!dictionary.TryGetValue(key(document), out var group))
                {
                    group = new List<TInclude>();
                    dictionary[key(document)] = group;
                }

                group.Add(document);
            }));
    }

    /// <inheritdoc cref="Include{T,TKey,TInclude}(IQueryable{T},Expression{Func{T,object}},Expression{Func{TInclude,TKey}},IDictionary{TKey,IList{TInclude}},Expression{Func{TInclude,bool}})" />
    /// <remarks>
    ///     Carried separately because <c>Dictionary&lt;TKey, List&lt;TInclude&gt;&gt;</c> does not
    ///     implement <c>IDictionary&lt;TKey, IList&lt;TInclude&gt;&gt;</c> — the value type is
    ///     invariant — so without this the most natural declaration would not bind. Marten carries the
    ///     same pair for the same reason.
    /// </remarks>
    public static IQueryable<T> Include<T, TKey, TInclude>(this IQueryable<T> queryable,
        Expression<Func<T, object?>> idSource, Expression<Func<TInclude, TKey>> idMapping,
        IDictionary<TKey, List<TInclude>> dictionary,
        Expression<Func<TInclude, bool>>? filter = null)
        where T : notnull where TKey : notnull where TInclude : notnull
    {
        ArgumentNullException.ThrowIfNull(idMapping);
        ArgumentNullException.ThrowIfNull(dictionary);

        var key = idMapping.Compile();

        return Attach(queryable, new IncludePlan<T, TInclude>(Compile(idSource), idMapping, filter,
            document =>
            {
                if (!dictionary.TryGetValue(key(document), out var group))
                {
                    group = [];
                    dictionary[key(document)] = group;
                }

                group.Add(document);
            }));
    }

    // ---- plumbing ----

    /// <summary>
    ///     The node an <c>Include</c> leaves in the expression tree.
    /// </summary>
    /// <remarks>
    ///     Never invoked. It exists so the plan can travel as a <see cref="ConstantExpression" />
    ///     argument of a <see cref="MethodCallExpression" /> whose first argument is the source, which
    ///     is the shape every walker in the provider already understands —
    ///     <c>SourceTypeFor</c> reaches the root through it and
    ///     <see cref="Parsing.LinqQueryParser" /> sees it as one more call in the chain.
    /// </remarks>
    internal static IQueryable<T> IncludeMarker<T>(IQueryable<T> source, IIncludePlan plan)
        => throw new NotSupportedException(
            "Include() is a marker in the expression tree and is never executed directly.");

    private static IQueryable<T> Attach<T>(IQueryable<T> queryable, IIncludePlan plan) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(queryable);

        if (queryable.Provider is not FisherQueryProvider)
        {
            throw new NotSupportedException(
                "Include() is only supported on a Fisher query started from session.Query<T>().");
        }

        return queryable.Provider.CreateQuery<T>(
            Expression.Call(MarkerMethod.MakeGenericMethod(typeof(T)), queryable.Expression,
                Expression.Constant(plan, typeof(IIncludePlan))));
    }

    /// <summary>
    ///     The id source, compiled once at the call rather than translated to SQL.
    /// </summary>
    /// <remarks>
    ///     Running it against the loaded parent documents is what makes the whole plan work without a
    ///     temp table, and it means the lambda may be anything the CLR can evaluate rather than
    ///     anything Fisher can translate. The <em>mapping</em> side has no such freedom, since that one
    ///     really does become a <c>WHERE</c>.
    /// </remarks>
    private static Func<T, object?> Compile<T>(Expression<Func<T, object?>> idSource)
    {
        ArgumentNullException.ThrowIfNull(idSource);
        return idSource.Compile();
    }

    /// <summary>
    ///     Reads the identity of an included document, for the dictionary plan that keys by it.
    /// </summary>
    /// <remarks>
    ///     Resolved through the same <c>AggregateIdentity.FindIdMember</c> the document mapping uses,
    ///     so a type whose identity Fisher would not store is refused here with the same answer it
    ///     would give at configuration time. Checked at the <c>Include</c> call rather than when the
    ///     query runs, because a key type that cannot possibly match is a mistake in the code and not
    ///     in the data.
    /// </remarks>
    private static Func<TInclude, TKey> IdentityAccessor<TKey, TInclude>()
        where TKey : notnull where TInclude : notnull
    {
        var member = AggregateIdentity.FindIdMember(typeof(TInclude))
                     ?? throw new NotSupportedException(
                         $"Include() cannot key a dictionary by the identity of '{typeof(TInclude).Name}', "
                         + "which has no identity member Fisher recognises.");

        var memberType = member is PropertyInfo property
            ? property.PropertyType
            : ((FieldInfo)member).FieldType;

        if (memberType != typeof(TKey))
        {
            throw new NotSupportedException(
                $"Include() cannot key a dictionary of '{typeof(TInclude).Name}' by '{typeof(TKey).Name}': "
                + $"its identity is a '{memberType.Name}'. Use a dictionary with that key type, or the "
                + "overload taking an explicit id mapping.");
        }

        var parameter = Expression.Parameter(typeof(TInclude), "x");

        return Expression.Lambda<Func<TInclude, TKey>>(
            Expression.MakeMemberAccess(parameter, member), parameter).Compile();
    }
}
