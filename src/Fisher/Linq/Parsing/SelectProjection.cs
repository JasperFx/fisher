using System.Linq.Expressions;
using Fisher.Linq.Members;
using Fisher.Linq.SqlGeneration;

namespace Fisher.Linq.Parsing;

/// <summary>
///     A <c>Select</c> lambda, taken apart into the columns it needs and a factory that rebuilds the
///     result from them (fisher#23).
/// </summary>
/// <remarks>
///     <para>
///         This is what turns <c>Select(x =&gt; x.Name)</c> from "read every document and pick a
///         member" into "read one JSON scalar per row". Over a table of large documents that is the
///         whole cost of the query, and before this the expensive shape was the only one available —
///         Fisher refused the projection rather than answering it inefficiently, which is the right
///         refusal but leaves the caller nowhere to go.
///     </para>
///     <para>
///         The mechanism is a rewrite rather than an interpreter. Every document member reachable in
///         the lambda body is collected and replaced by an indexer into an <c>object?[]</c> of values
///         read from the row; the rewritten body is then compiled once. So the shape of the projection —
///         anonymous type, constructor, object initialiser, string concatenation, arithmetic — is
///         whatever C# already allowed, and none of it needs translating to SQL.
///     </para>
///     <para>
///         The consequence, and it is deliberate: <b>only the member accesses become columns.</b>
///         Everything around them runs in .NET, per row. <c>Select(x =&gt; x.First + " " + x.Last)</c>
///         reads two columns and concatenates client-side rather than emitting SQL concatenation.
///         That is the same answer Marten gives, and it keeps the surface honest — there is no set of
///         expressions that silently falls back to reading whole documents.
///     </para>
/// </remarks>
internal sealed class SelectProjection
{
    private SelectProjection(string[] locators, Type[] columnTypes, Func<object?[], object?> build,
        Type resultType, bool requiresFullTextJoin)
    {
        Locators = locators;
        ColumnTypes = columnTypes;
        Build = build;
        ResultType = resultType;
        RequiresFullTextJoin = requiresFullTextJoin;
    }

    /// <summary>The SQL locators to select, in order.</summary>
    public string[] Locators { get; }

    /// <summary>The CLR type each column should be read as, parallel to <see cref="Locators" />.</summary>
    public Type[] ColumnTypes { get; }

    /// <summary>Rebuilds one result from one row's values.</summary>
    public Func<object?[], object?> Build { get; }

    public Type ResultType { get; }

    /// <summary>
    ///     True when the projection reads a value the FTS5 match COMPUTES — a snippet or a highlight —
    ///     which is what makes the query's full-text predicate a join rather than a sub-select
    ///     (fisher#220).
    /// </summary>
    public bool RequiresFullTextJoin { get; }

    public static SelectProjection For(LambdaExpression selector, IMemberResolver members)
    {
        var collector = new MemberCollector(members, selector.Parameters[0]);
        var body = collector.Visit(selector.Body)!;

        if (collector.Locators.Count == 0)
        {
            throw new BadLinqExpressionException(
                "A Select must project at least one document member. A projection of constants only "
                + "would read no columns, which is almost certainly not what was meant.");
        }

        var values = collector.Values;

        // Boxed to object? so one delegate type serves every projection shape; the provider casts
        // back to the result type it was asked for.
        var lambda = Expression.Lambda<Func<object?[], object?>>(
            Expression.Convert(body, typeof(object)), values);

        return new SelectProjection(
            collector.Locators.ToArray(),
            collector.ColumnTypes.ToArray(),
            lambda.Compile(),
            selector.ReturnType,
            collector.RequiresFullTextJoin);
    }

    /// <summary>
    ///     Replaces each document member access with a read from the row's value array, recording the
    ///     locator it needs.
    /// </summary>
    /// <remarks>
    ///     Members are deduplicated by locator, so <c>x =&gt; new { A = x.N, B = x.N * 2 }</c> selects
    ///     <c>n</c> once.
    /// </remarks>
    private sealed class MemberCollector : ExpressionVisitor
    {
        private readonly IMemberResolver _members;
        private readonly ParameterExpression _document;
        private readonly Dictionary<string, int> _indexes = new();

        public MemberCollector(IMemberResolver members, ParameterExpression document)
        {
            _members = members;
            _document = document;
        }

        public ParameterExpression Values { get; } = Expression.Parameter(typeof(object?[]), "values");

        public List<string> Locators { get; } = [];

        public List<Type> ColumnTypes { get; } = [];

        /// <summary>Set when a snippet or highlight was projected -- fisher#220.</summary>
        public bool RequiresFullTextJoin { get; private set; }

        /// <summary>
        ///     <c>Snippet()</c> and <c>Highlight()</c>, which are columns without being members.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///     They read a value the FTS5 match computes, so there is nothing on the document to
        ///     resolve them to -- they become a locator here the same way a member does, and the rest
        ///     of the projection machinery cannot tell the difference. That is the whole reason a
        ///     projected snippet costs so little: the rewrite already replaces arbitrary sub-expressions
        ///     with reads from the row's value array.
        ///     </para>
        ///     <para>
        ///     Marker text is captured at translation time, like every other argument the full-text
        ///     operators take. A marker that cannot be evaluated when the query is built is refused
        ///     rather than deferred.
        ///     </para>
        /// </remarks>
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType != typeof(FullTextSearchExtensions))
            {
                return base.VisitMethodCall(node);
            }

            var method = node.Method.Name;

            if (method is not ("Snippet" or "Highlight"))
            {
                throw new BadLinqExpressionException(
                    $"'{method}' filters rows and belongs in a Where, not in a Select -- it answers "
                    + "whether a document matched, which is not a value to project.");
            }

            var mapping = _members.Mapping
                          ?? throw new BadLinqExpressionException(
                              $"'{method}' can only be used against a document type.");

            var index = mapping.FullTextIndex
                        ?? throw new BadLinqExpressionException(
                            $"'{mapping.DocumentType.Name}' declares no full-text index, so there is "
                            + $"nothing for '{method}' to read.");

            var table = Weasel.Sqlite.SchemaUtils.QuoteName(
                Storage.FullText.FullTextSchema.TableNameFor(mapping).Name);

            var locator = method == "Snippet"
                ? SnippetLocator(node, table)
                : HighlightLocator(node, table, index, mapping.DocumentType.Name);

            RequiresFullTextJoin = true;

            if (!_indexes.TryGetValue(locator, out var slot))
            {
                slot = Locators.Count;
                _indexes[locator] = slot;
                Locators.Add(locator);
                ColumnTypes.Add(typeof(string));
            }

            return Expression.Convert(
                Expression.ArrayIndex(Values, Expression.Constant(slot)), node.Type);
        }

        private static string SnippetLocator(MethodCallExpression node, string table)
            => node.Arguments.Count == 1
                ? FullTextExtract.Snippet(table, FullTextExtract.BestColumn, "<b>", "</b>", "\u2026", 32)
                : FullTextExtract.Snippet(
                    table,
                    FullTextExtract.BestColumn,
                    Argument<string>(node, 1, "startMarker"),
                    Argument<string>(node, 2, "endMarker"),
                    Argument<string>(node, 3, "ellipsis"),
                    Argument<int>(node, 4, "maxTokens"));

        private static string HighlightLocator(MethodCallExpression node, string table,
            Storage.FullText.FullTextIndex index, string documentType)
        {
            var column = Argument<string>(node, 1, "column");
            var ordinal = Array.FindIndex(index.MemberNames,
                name => string.Equals(name, column, StringComparison.OrdinalIgnoreCase));

            if (ordinal < 0)
            {
                throw new BadLinqExpressionException(
                    $"'{column}' is not an indexed member of '{documentType}', so Highlight has no "
                    + $"column to read. The index covers: {string.Join(", ", index.MemberNames)}.");
            }

            return node.Arguments.Count == 2
                ? FullTextExtract.Highlight(table, ordinal, "<b>", "</b>")
                : FullTextExtract.Highlight(table, ordinal,
                    Argument<string>(node, 2, "startMarker"),
                    Argument<string>(node, 3, "endMarker"));
        }

        private static T Argument<T>(MethodCallExpression node, int position, string name)
        {
            var value = WhereClauseParser.ExtractValue(node.Arguments[position]);

            return value is T typed
                ? typed
                : throw new BadLinqExpressionException(
                    $"'{node.Method.Name}' requires a '{name}' that can be evaluated when the query "
                    + "is built.");
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (!IsDocumentMember(node))
            {
                return base.VisitMember(node);
            }

            var member = _members.ResolveMember(node);

            if (!_indexes.TryGetValue(member.TypedLocator, out var index))
            {
                index = Locators.Count;
                _indexes[member.TypedLocator] = index;
                Locators.Add(member.TypedLocator);
                ColumnTypes.Add(node.Type);
            }

            // The value arrives already converted to the member's CLR type by the provider, so this is
            // an unbox rather than a coercion.
            return Expression.Convert(
                Expression.ArrayIndex(Values, Expression.Constant(index)), node.Type);
        }

        /// <summary>
        ///     Whether this member chain is rooted at the document parameter — as opposed to a captured
        ///     local, a static, or a member of something the caller closed over.
        /// </summary>
        private bool IsDocumentMember(MemberExpression node)
        {
            Expression? current = node;

            while (current is MemberExpression member)
            {
                current = member.Expression;
            }

            return current == _document;
        }
    }
}
