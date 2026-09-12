using System.Linq.Expressions;
using System.Reflection;
using Fisher.Internal;
using Fisher.Linq.Members;
using Fisher.Storage;
using Fisher.Storage.Vectors;
using JasperFx.Events.Vectors;

namespace Fisher;

/// <summary>
///     Vector similarity search over a declared embedding member (fisher#241), on Marten.PgVector's
///     API shape so application code written against one store reads the same against the other.
/// </summary>
/// <remarks>
///     <para>
///         Both overloads run one SQL statement on the session's own connection through
///         <see cref="IAdvancedSql" />: the document's columns, plus <c>fi_vector_distance</c> over the
///         member's <c>json_extract</c> locator and the query vector bound as a float32 BLOB, ordered
///         by that distance, limited. The soft-delete filter applies as it does to every query;
///         tenancy needs nothing, because a tenant is its own database.
///     </para>
///     <para>
///         The document's identity map is not consulted (the rows come back through the same selector
///         <c>Query&lt;T&gt;()</c> uses), and a query vector whose length is not the index's declared
///         <c>dimensions</c> is refused before any SQL runs. A stored vector of the wrong length fails
///         the row at query time rather than scoring it — see <c>VectorFunctions</c>.
///     </para>
/// </remarks>
public static class VectorSearchExtensions
{
    /// <summary>
    ///     The <paramref name="limit" /> documents nearest to <paramref name="query" /> under the
    ///     index's distance (or <paramref name="distance" /> when given), nearest first.
    /// </summary>
    public static async Task<IReadOnlyList<T>> VectorSearchAsync<T>(
        this IQuerySession session,
        Expression<Func<T, object?>> member,
        ReadOnlyMemory<float> query,
        int limit = 10,
        DistanceFunction? distance = null,
        CancellationToken token = default) where T : notnull
    {
        var (sql, parameters) = Build(session, member, query, limit, distance, withScore: false);
        return await session.AdvancedSql.QueryAsync<T>(sql, token, parameters).ConfigureAwait(false);
    }

    /// <summary>
    ///     The same search, each document paired with its distance — smaller is closer under every
    ///     metric — for a similarity floor, or for fusing with a full-text ranking.
    /// </summary>
    public static async Task<IReadOnlyList<VectorMatch<T>>> VectorSearchWithScoresAsync<T>(
        this IQuerySession session,
        Expression<Func<T, object?>> member,
        ReadOnlyMemory<float> query,
        int limit = 10,
        DistanceFunction? distance = null,
        CancellationToken token = default) where T : notnull
    {
        var (sql, parameters) = Build(session, member, query, limit, distance, withScore: true);
        var rows = await session.AdvancedSql.QueryAsync<T, double>(sql, token, parameters).ConfigureAwait(false);
        return rows.Select(row => new VectorMatch<T>(row.Item1, row.Item2)).ToList();
    }

    private static (string Sql, object?[] Parameters) Build<T>(
        IQuerySession session,
        Expression<Func<T, object?>> member,
        ReadOnlyMemory<float> query,
        int limit,
        DistanceFunction? distance,
        bool withScore)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit must be at least 1");

        var options = ((FisherSession)session).Options;
        var mapping = options.Schema.MappingFor(typeof(T));
        var chain = ChainOf(member);
        var index = mapping.FindVectorIndex(chain)
                    ?? throw new InvalidOperationException(
                        mapping.VectorIndexes.Count == 0
                            ? $"'{typeof(T).Name}' declares no vector index, so there is nothing to search. Declare one with "
                              + $"Schema.For<{typeof(T).Name}>().VectorIndex(x => x.{string.Join(".", chain.Select(m => m.Name))}, dimensions) "
                              + "or [VectorIndex(dimensions)] on the member."
                            : $"'{typeof(T).Name}.{string.Join(".", chain.Select(m => m.Name))}' is not a declared vector member. Declared: "
                              + string.Join(", ", mapping.VectorIndexes.Select(v => v.MemberName)) + ".");

        if (query.Length != index.Dimensions)
        {
            throw new ArgumentException(
                $"The query vector has {query.Length} dimensions but '{typeof(T).Name}.{index.MemberName}' was declared with {index.Dimensions}.",
                nameof(query));
        }

        var members = new MemberFactory(options, mapping);
        var locator = index.Locator(members);
        var metric = VectorFunctions.MetricName(distance ?? index.Distance);
        var fields = string.Join(", ", session.AdvancedSql.SelectFieldsFor<T>());
        var scoreColumn = $"{VectorFunctions.DistanceFunctionName}(?, {locator}, ?)";

        var sql = $"select {fields}, {scoreColumn} as distance from {mapping.QuotedTableName} where {locator} is not null";
        if (mapping.IsSoftDeleted) sql += $" and {SoftDelete.NotDeletedSql}";
        sql += " order by distance limit ?";

        // The score column is selected either way: SQLite's ORDER BY can name a select-list alias,
        // and a document-only read simply ignores a trailing column it was not asked for.
        _ = withScore;
        return (sql, [metric, VectorFunctions.ToBlob(query.Span), limit]);
    }

    /// <summary>The member chain of <c>x =&gt; x.A.B</c>, unwrapping the boxing convert.</summary>
    private static MemberInfo[] ChainOf<T>(Expression<Func<T, object?>> member)
    {
        var body = member.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        var chain = new List<MemberInfo>();
        while (body is MemberExpression access)
        {
            chain.Insert(0, access.Member);
            body = access.Expression!;
        }

        if (chain.Count == 0 || body is not ParameterExpression)
        {
            throw new ArgumentException(
                "The vector member must be a plain member access on the document, like x => x.Embedding or x => x.Content.Vector.",
                nameof(member));
        }

        return chain.ToArray();
    }
}
