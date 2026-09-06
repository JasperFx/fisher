using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Fisher.Internal;

namespace Fisher.Linq.Includes;

/// <summary>
///     An <c>Include()</c> resolved as a second query rather than as a join.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a second query and not Marten's temporary table.</b> Marten writes the parent
///         query's identities into a temp table and joins the included tables to it, and on Postgres
///         that is the right shape: each extra statement is another network round trip, so collapsing
///         <em>n</em> reads into one is worth a good deal of machinery. Fisher runs in-process against
///         a file. There is no round trip to amortise — a second <c>SELECT</c> on the session's own
///         open connection costs a statement prepare and a b-tree seek — so the temp table would buy
///         nothing and cost the statement builder a whole second shape to maintain.
///     </para>
///     <para>
///         <b>The consequence, stated rather than hidden.</b> The parent rows are materialized
///         <em>first</em>, and the identity values come out of the loaded documents by running the
///         caller's <c>idSource</c> lambda over them in memory. That is what makes the plan indifferent
///         to how complicated the lambda is, and it is also why <c>Include()</c> cannot follow a
///         <c>Select</c>, a <c>GroupBy</c> or a join: those produce rows that are not parent documents,
///         so there is nothing to run the lambda against. Those combinations are refused by name in
///         <see cref="Parsing.LinqQueryParser" /> rather than quietly leaving the destination empty.
///     </para>
///     <para>
///         <b>Reads are not atomic with the parent read.</b> Two statements are two reads. Inside an
///         explicit transaction, or against SQLite's snapshot in WAL mode, they see the same data;
///         without one, a concurrent writer can land between them. Marten's temp-table join has the
///         same exposure whenever the batch is not wrapped in a transaction, so this is a shared
///         property rather than a Fisher-specific one — but Fisher's is easier to reach, and saying so
///         is cheaper than having someone find it.
///     </para>
/// </remarks>
/// <typeparam name="TParent">The document type the query returns.</typeparam>
/// <typeparam name="TInclude">The related document type being fetched.</typeparam>
internal sealed class IncludePlan<TParent, TInclude> : IIncludePlan where TInclude : notnull
{
    /// <summary>
    ///     How many identity values go into one <c>in (...)</c>.
    /// </summary>
    /// <remarks>
    ///     Every value binds as a parameter, and SQLite caps a statement's parameter count —
    ///     999 on builds older than 3.32 and 32766 on newer ones. Chunking well under the lower
    ///     bound means the plan never has to know which build it is running on, and a chunked read on
    ///     an embedded store is several b-tree seeks rather than several round trips.
    /// </remarks>
    private const int ChunkSize = 500;

    private static readonly MethodInfo ContainsMethod = typeof(Enumerable)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(x => x.Name == nameof(Enumerable.Contains) && x.GetParameters().Length == 2);

    private readonly Action<TInclude> _callback;
    private readonly Expression<Func<TInclude, bool>>? _filter;
    private readonly Func<TParent, object?> _idSource;

    /// <summary>
    ///     The member of <typeparamref name="TInclude" /> the parent's value is matched against, or
    ///     null to match the included document's own identity.
    /// </summary>
    private readonly LambdaExpression? _idMapping;

    public IncludePlan(Func<TParent, object?> idSource, LambdaExpression? idMapping,
        Expression<Func<TInclude, bool>>? filter, Action<TInclude> callback)
    {
        _idSource = idSource;
        _idMapping = idMapping;
        _filter = filter;
        _callback = callback;
    }

    public Type IncludeType => typeof(TInclude);

    public async Task ResolveAsync(FisherSession session, IReadOnlyList<object> parents,
        CancellationToken token)
    {
        var (elementType, matchBody, parameter) = ResolveMatch(session);

        var values = DistinctValues(parents, elementType);

        if (values.Count == 0)
        {
            return;
        }

        for (var start = 0; start < values.Count; start += ChunkSize)
        {
            var chunk = Slice(values, elementType, start, Math.Min(ChunkSize, values.Count - start));

            var predicate = Expression.Lambda<Func<TInclude, bool>>(
                Expression.Call(ContainsMethod.MakeGenericMethod(elementType),
                    Expression.Constant(chunk, typeof(IEnumerable<>).MakeGenericType(elementType)),
                    matchBody),
                parameter);

            var query = session.Query<TInclude>().Where(predicate);

            if (_filter is not null)
            {
                query = query.Where(_filter);
            }

            foreach (var document in await query.ToListAsync(token).ConfigureAwait(false))
            {
                _callback(document);
            }
        }
    }

    /// <summary>
    ///     The member of the included document the parent's value is matched against, and the CLR type
    ///     that member's values have.
    /// </summary>
    /// <remarks>
    ///     A nullable mapping member is unwrapped with a <c>Convert</c>, which the where parser strips
    ///     before resolving the member — so <c>Guid?</c> and <c>Guid</c> reach the same locator and the
    ///     identity values only ever have to be built as one type.
    /// </remarks>
    private (Type ElementType, Expression MatchBody, ParameterExpression Parameter) ResolveMatch(
        FisherSession session)
    {
        ParameterExpression parameter;
        Expression body;

        if (_idMapping is null)
        {
            parameter = Expression.Parameter(typeof(TInclude), "x");
            body = Expression.MakeMemberAccess(parameter,
                session.Options.Schema.MappingFor(typeof(TInclude)).IdMember);
        }
        else
        {
            parameter = _idMapping.Parameters[0];
            body = _idMapping.Body;
        }

        var elementType = Nullable.GetUnderlyingType(body.Type) ?? body.Type;

        if (elementType != body.Type)
        {
            body = Expression.Convert(body, elementType);
        }

        return (elementType, body, parameter);
    }

    /// <summary>
    ///     Every distinct, non-null identity the parent rows carry, in the order first seen.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A collection member fans out: <c>Include(x =&gt; x.CrewIds, crew)</c> contributes every
    ///         element. A <see cref="string" /> is a value here rather than a sequence of characters,
    ///         which is the one special case an <see cref="IEnumerable" /> test gets wrong.
    ///     </para>
    ///     <para>
    ///         Distinct because the destination takes one call per included <em>document</em>, not one
    ///         per parent reference — the same contract Marten's reader has, since a join over a temp
    ///         table of distinct identities yields each included row once. Ten catches by one angler
    ///         add the angler to an <c>IList</c> once.
    ///     </para>
    /// </remarks>
    private List<object> DistinctValues(IReadOnlyList<object> parents, Type elementType)
    {
        var values = new List<object>();
        var seen = new HashSet<object>();

        foreach (var parent in parents)
        {
            var raw = _idSource((TParent)parent);

            if (raw is null)
            {
                continue;
            }

            if (raw is IEnumerable sequence and not string)
            {
                foreach (var item in sequence)
                {
                    if (item is not null && seen.Add(item))
                    {
                        values.Add(Coerce(item, elementType));
                    }
                }
            }
            else if (seen.Add(raw))
            {
                values.Add(Coerce(raw, elementType));
            }
        }

        return values;
    }

    /// <summary>
    ///     A typed array of one chunk of the identity values.
    /// </summary>
    /// <remarks>
    ///     Typed rather than <c>object[]</c> because <c>Enumerable.Contains&lt;T&gt;</c> is closed over
    ///     the member's type, and the constant has to be an <c>IEnumerable&lt;T&gt;</c> of it for the
    ///     expression to be well-formed.
    /// </remarks>
    private static Array Slice(List<object> values, Type elementType, int start, int length)
    {
        var array = Array.CreateInstance(elementType, length);

        for (var i = 0; i < length; i++)
        {
            array.SetValue(values[start + i], i);
        }

        return array;
    }

    /// <summary>
    ///     Refuses a parent identity whose type the included member cannot hold, rather than binding a
    ///     value that will match nothing.
    /// </summary>
    /// <remarks>
    ///     This is the silent-failure shape the house rule exists for: <c>in (@p0)</c> against a
    ///     mistyped parameter is valid SQL that returns no rows, so an <c>Include</c> joining a
    ///     <c>string</c> member to a <c>Guid</c> identity would leave the destination empty and say
    ///     nothing. The numeric widenings are allowed because an <c>int</c> parent member against a
    ///     <c>long</c> identity is a real and unambiguous pairing.
    /// </remarks>
    private static object Coerce(object value, Type elementType)
    {
        if (elementType.IsInstanceOfType(value))
        {
            return value;
        }

        if (value is IConvertible && elementType.IsPrimitive)
        {
            try
            {
                return Convert.ChangeType(value, elementType,
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
            {
                // Falls through to the refusal below, which names both types.
            }
        }

        throw new BadLinqExpressionException(
            $"Include() cannot match a '{value.GetType().Name}' from {typeof(TParent).Name} against the "
            + $"'{elementType.Name}' member of {typeof(TInclude).Name}. The two would compare as "
            + "different types in SQLite and the include would come back empty rather than failing, so "
            + "it is refused here. Check that the id source names the member holding the related "
            + "document's identity.");
    }
}
