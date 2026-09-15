using System.Linq.Expressions;
using System.Reflection;
using Fisher.Linq.Members;
using Fisher.Linq.SqlGeneration;
using Fisher.Storage.Vectors;
using JasperFx.Events.Vectors;
using Weasel.Core;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq;

public partial class FisherQueryProvider
{
    /// <summary>
    ///     Vector similarity search, built as an ordinary <see cref="Statement" /> over
    ///     <c>Query&lt;T&gt;()</c> (fisher#241, fisher#285, fisher#291).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>⚠️ The whole point of it living here is the WHERE clause.</b> The first version built
    ///         its own SQL string in <c>VectorSearchExtensions</c> and restated one filter — the
    ///         soft-delete one — from memory. It therefore ignored the two it did not restate:
    ///         conjoined tenancy, so a session opened for tenant <c>a</c> ranked tenant <c>b</c>'s rows
    ///         and returned them, and the <c>doc_type</c> discriminator, so a search over a subclass
    ///         read its siblings' rows (fisher#285). Going through
    ///         <see cref="FisherQueryProvider.BuildStatement" /> means the filters are the ones
    ///         <c>Query&lt;T&gt;()</c> applies, from the same code, so the next one added to LINQ cannot
    ///         be missed here again.
    ///     </para>
    ///     <para>
    ///         It is also what makes the <c>filter</c> predicate of
    ///         <see cref="IDocumentSearchOperations" /> (jasperfx#843) free: the caller's expression is
    ///         appended as an ordinary <c>Where</c> on that queryable, so it supports and refuses
    ///         exactly what <c>Query&lt;T&gt;().Where(...)</c> does, and it is applied <em>before</em>
    ///         the limit — Fisher scans every row, so the result is the true top-k of the filtered set
    ///         with no recall caveat at all.
    ///     </para>
    ///     <para>
    ///         Two knock-on differences from the old raw-SQL path, both towards
    ///         <c>Query&lt;T&gt;()</c>'s behaviour rather than away from it: the rows materialize
    ///         through the session's own storage (so a tracking session tracks them, as it does for
    ///         every other query), and the statement runs outside
    ///         <c>StoreOptions.ResiliencePipeline</c>, which no LINQ query has ever run inside.
    ///     </para>
    /// </remarks>
    internal async Task<IReadOnlyList<VectorMatch<T>>> VectorSearchAsync<T>(
        Expression<Func<T, object?>> member,
        ReadOnlyMemory<float> query,
        int limit,
        DistanceFunction? distance,
        Expression<Func<T, bool>>? filter,
        CancellationToken token) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(member);
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit must be at least 1");

        var mapping = _session.Options.Schema.MappingFor(typeof(T));
        var chain = ChainOf(member);
        var index = mapping.FindVectorIndex(chain) ?? throw NoSuchVectorMember<T>(mapping, chain);

        if (query.Length != index.Dimensions)
        {
            throw new ArgumentException(
                $"The query vector has {query.Length} dimensions but '{typeof(T).Name}.{index.MemberName}' was declared with {index.Dimensions}.",
                nameof(query));
        }

        IQueryable<T> queryable = _session.Query<T>();
        if (filter is not null)
        {
            queryable = queryable.Where(filter);
        }

        var (statement, selector) = Build<T>(queryable.Expression);

        var locator = index.Locator(new MemberFactory(_session.Options, mapping));

        statement.SelectFragment = new VectorDistanceSelect(
            statement.SelectColumns,
            VectorFunctions.MetricName(distance ?? index.Distance),
            locator,
            VectorFunctions.ToBlob(query.Span));

        // A row with no embedding is skipped rather than scored. Ahead of the ordering because the
        // distance function refuses a stored vector of the wrong length, and "absent" is not that.
        statement.Wheres.Add(new LiteralSqlFragment($"{locator} is not null"));

        // The caller's own ordering, if the filter expression somehow carried one, means nothing to a
        // search whose whole contract is "nearest first".
        statement.OrderBys.Clear();
        statement.OrderBys.Add((VectorDistanceSelect.Alias, false));
        statement.Limit = limit;
        statement.Offset = null;

        var results = new List<VectorMatch<T>>();

        await using var reader = await ExecuteReaderAsync(statement, token).ConfigureAwait(false);

        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var document = await selector.ResolveAsync(reader, token).ConfigureAwait(false);

            // Last column by construction: the select fragment appends it after the storage's own.
            var scored = reader.FieldCount - 1;
            var score = await reader.IsDBNullAsync(scored, token).ConfigureAwait(false)
                ? double.NaN
                : reader.GetDouble(scored);

            results.Add(new VectorMatch<T>(document, score));
        }

        return results;
    }

    private static InvalidOperationException NoSuchVectorMember<T>(Storage.DocumentMapping mapping,
        MemberInfo[] chain)
        => new(mapping.VectorIndexes.Count == 0
            ? $"'{typeof(T).Name}' declares no vector index, so there is nothing to search. Declare one with "
              + $"Schema.For<{typeof(T).Name}>().VectorIndex(x => x.{string.Join(".", chain.Select(m => m.Name))}, dimensions) "
              + "or [VectorIndex(dimensions)] on the member."
            : $"'{typeof(T).Name}.{string.Join(".", chain.Select(m => m.Name))}' is not a declared vector member. Declared: "
              + string.Join(", ", mapping.VectorIndexes.Select(v => v.MemberName)) + ".");

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

    /// <summary>
    ///     The storage's own select list plus <c>fi_vector_distance(metric, locator, query) as distance</c>.
    /// </summary>
    /// <remarks>
    ///     The score is selected whether or not the caller asked for it, because SQLite's
    ///     <c>ORDER BY</c> can name a select-list alias and repeating the expression there would bind
    ///     the query vector's BLOB a second time. A document-only read simply ignores a trailing column,
    ///     and the selectors read by fixed index from the left.
    /// </remarks>
    private sealed class VectorDistanceSelect(string columns, string metric, string locator, byte[] query)
        : ISqlFragment
    {
        internal const string Alias = "distance";

        public void Apply(ICommandBuilder builder)
        {
            builder.Append(columns);
            builder.Append(", ");
            builder.Append(VectorFunctions.DistanceFunctionName);
            builder.Append('(');
            builder.AppendParameter(metric);
            builder.Append(", ");
            builder.Append(locator);
            builder.Append(", ");
            builder.AppendParameter(query);
            builder.Append(") as ");
            builder.Append(Alias);
        }
    }
}
