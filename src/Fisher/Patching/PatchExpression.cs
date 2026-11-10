using System.Linq.Expressions;
using Fisher.Linq.Members;
using Fisher.Linq.Parsing.Methods;
using Fisher.Storage;
using JasperFx.Events;

namespace Fisher.Patching;

/// <summary>
///     Fisher's implementation of the shared <see cref="IPatchExpression{T}" /> — changing part of a stored
///     document without loading it (fisher#35, fisher#465).
/// </summary>
/// <remarks>
///     <para>
///         <b>The interface is <c>JasperFx.Events.IPatchExpression&lt;T&gt;</c></b> (jasperfx#331), shared
///         with Marten and Polecat, and Fisher's own copy is gone as of 2.0. Adopting it changed three
///         defaults, each to Marten's meaning: <c>Insert</c> with no index appends rather than inserting at
///         the front, <c>Remove</c> takes the first match unless told <see cref="RemoveAction.RemoveAll" />,
///         and <c>Duplicate</c> copies the source member's stored value rather than setting a value
///         everywhere (<c>Set</c> then <c>Duplicate</c> is the old spelling). The nullable
///         <c>Increment</c> overloads the shared interface does not declare are extension methods on
///         <see cref="PatchExpressionExtensions" />.
///     </para>
///     <para>
///         <b>This is one of the strongest cases for SQLite specifically.</b> Every operation is a
///         single json1 function inside one <c>update … set data = …</c>: no server function to install
///         (Marten needs a PL/pgSQL patch function), no <c>JSON_MODIFY</c> shape differences to work
///         around, and they compose — several calls nest into one expression and one statement.
///     </para>
///     <para>
///         <b>A duplicated field follows a patch with nothing to refresh</b>, because fisher#2 made
///         duplicated fields <c>VIRTUAL</c> generated columns over <c>data</c>. Marten and Polecat must
///         update theirs inside the patch SQL. That is the clearest single dividend of that decision.
///     </para>
///     <para>
///         <b>What a patch costs, said plainly:</b> <c>json_set</c> re-renders the whole document, so a
///         patched row is no longer byte-identical to what the serializer would have written — a key
///         added or renamed lands at the end. It avoids the deserialize/mutate/serialize round trip, not
///         the row rewrite. Do not let "patching avoids the round trip" imply "patching is cheap".
///     </para>
///     <para>
///         The string overloads take the <em>stored</em> key — <c>"name"</c>, not <c>"Name"</c>, under the
///         default camelCase policy — because the point of them is reaching keys the type no longer has a
///         member for.
///     </para>
/// </remarks>
internal sealed class PatchExpression<T> : IPatchExpression<T> where T : notnull
{
    private readonly MemberFactory _members;
    private readonly Serialization.ISerializer _serializer;
    private readonly PatchOperation _operation;

    internal PatchExpression(MemberFactory members, Serialization.ISerializer serializer,
        PatchOperation operation)
    {
        _members = members;
        _serializer = serializer;
        _operation = operation;
    }

    // ---- Set / Duplicate ----

    public IPatchExpression<T> Set<TValue>(Expression<Func<T, TValue>> expression, TValue value)
        => SetPath(PathOf(expression), value);

    public IPatchExpression<T> Set<TValue>(string name, TValue value)
        => SetPath(PathOfStoredKey(name), value);

    public IPatchExpression<T> Set<TParent, TValue>(string name, Expression<Func<T, TParent>> expression,
        TValue value)
        => SetPath(ChildPath(PathOf(expression), name), value);

    /// <summary>
    ///     Copy the source member's <em>stored</em> value to every destination.
    /// </summary>
    /// <remarks>
    ///     <c>-&gt;</c> rather than <c>json_extract</c>, because it always returns JSON text, and the
    ///     surrounding <c>json(...)</c> then carries an object or array across as one rather than as a
    ///     string. An absent source copies as JSON null, as Marten's does.
    /// </remarks>
    public IPatchExpression<T> Duplicate<TElement>(Expression<Func<T, TElement>> expression,
        params Expression<Func<T, TElement>>[] destinations)
    {
        ArgumentNullException.ThrowIfNull(destinations);

        if (destinations.Length == 0)
        {
            throw new ArgumentException("Duplicate needs at least one destination.", nameof(destinations));
        }

        var source = PathOf(expression);

        foreach (var destination in destinations)
        {
            var target = PathOf(destination);
            _operation.Add((inner, _) => $"json_set({inner}, '{target}', json(({inner}) -> '{source}'))");
        }

        return this;
    }

    // ---- Increment ----

    public IPatchExpression<T> Increment(Expression<Func<T, int>> expression, int increment = 1)
        => IncrementPath(PathOf(expression), increment);

    public IPatchExpression<T> Increment(Expression<Func<T, long>> expression, long increment = 1)
        => IncrementPath(PathOf(expression), increment);

    public IPatchExpression<T> Increment(Expression<Func<T, double>> expression, double increment = 1)
        => IncrementPath(PathOf(expression), increment);

    public IPatchExpression<T> Increment(Expression<Func<T, float>> expression, float increment = 1)
        => IncrementPath(PathOf(expression), (double)increment);

    public IPatchExpression<T> Increment(Expression<Func<T, decimal>> expression, decimal increment = 1)
        => IncrementPath(PathOf(expression), (double)increment);

    /// <summary>
    ///     The nullable overloads Fisher 1.x declared, reached through <see cref="PatchExpressionExtensions" />.
    /// </summary>
    internal IPatchExpression<T> IncrementNullable(LambdaExpression expression, object increment)
        => IncrementPath(PathOf(expression), increment);

    // ---- Append ----

    public IPatchExpression<T> Append<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression,
        TElement element)
        => AppendPath(PathOf(expression), element);

    public IPatchExpression<T> Append<TElement>(Expression<Func<T, IDictionary<string, TElement>>> expression,
        string key, TElement element)
        => SetPath(KeyPath(PathOf(expression), key), element);

    public IPatchExpression<T> AppendIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression,
        TElement element)
    {
        var path = PathOf(expression);

        _operation.Add((inner, bind) =>
        {
            var parameter = bind(Json(element));

            return $"case when {ContainsValueSql(inner, path, parameter)} then {inner} "
                   + $"else json_insert({inner}, '{path}[#]', json({parameter})) end";
        });

        return this;
    }

    /// <remarks>
    ///     <c>json_insert</c>, which writes only where the path is absent — so an existing key keeps its
    ///     value, and an absent dictionary is created with the one entry.
    /// </remarks>
    public IPatchExpression<T> AppendIfNotExists<TElement>(
        Expression<Func<T, IDictionary<string, TElement>>> expression, string key, TElement element)
    {
        var path = KeyPath(PathOf(expression), key);

        _operation.Add((inner, bind) => $"json_insert({inner}, '{path}', json({bind(Json(element))}))");

        return this;
    }

    public IPatchExpression<T> AppendIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression,
        TElement element, Expression<Func<TElement, bool>> predicate)
    {
        var (path, alias, filter) = ElementPredicate(expression, predicate, nameof(AppendIfNotExists));

        _operation.Add((inner, bind) =>
            $"case when {MatchesSql(inner, path, alias, bind(filter))} then {inner} "
            + $"else json_insert({inner}, '{path}[#]', json({bind(Json(element))})) end");

        return this;
    }

    // ---- Insert ----

    /// <summary>
    ///     Insert into an array member at <paramref name="index" />, shifting the rest along (fisher#52);
    ///     with no index, append.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b><c>json_insert</c> is not the mechanism.</b> It only inserts where the path does not
    ///         already exist, so at an occupied index it is a silent no-op, and <c>json_replace</c> would
    ///         overwrite rather than shift. So the array is rebuilt with the new element placed by ordinal.
    ///     </para>
    ///     <para>
    ///         <b>An index past the end appends rather than throwing.</b> A patch does not read the
    ///         document, so refusing would mean a round trip to learn a length. A negative index is refused.
    ///     </para>
    /// </remarks>
    public IPatchExpression<T> Insert<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression,
        TElement element, int? index = null)
    {
        var path = PathOf(expression);

        if (index is null)
        {
            return AppendPath(path, element);
        }

        ArgumentOutOfRangeException.ThrowIfNegative(index.Value, nameof(index));

        _operation.Add((inner, bind) => InsertAtSql(inner, path, index.Value, bind(Json(element))));

        return this;
    }

    public IPatchExpression<T> InsertIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression,
        TElement element, int? index = null)
    {
        if (index is null)
        {
            return AppendIfNotExists(expression, element);
        }

        ArgumentOutOfRangeException.ThrowIfNegative(index.Value, nameof(index));

        var path = PathOf(expression);

        _operation.Add((inner, bind) =>
        {
            var parameter = bind(Json(element));

            return $"case when {ContainsValueSql(inner, path, parameter)} then {inner} "
                   + $"else {InsertAtSql(inner, path, index.Value, parameter)} end";
        });

        return this;
    }

    public IPatchExpression<T> InsertIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression,
        TElement element, Expression<Func<TElement, bool>> predicate, int? index = null)
    {
        if (index is not null)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index.Value, nameof(index));
        }

        var (path, alias, filter) = ElementPredicate(expression, predicate, nameof(InsertIfNotExists));

        _operation.Add((inner, bind) =>
        {
            var parameter = bind(Json(element));
            var insert = index is null
                ? $"json_insert({inner}, '{path}[#]', json({parameter}))"
                : InsertAtSql(inner, path, index.Value, parameter);

            return $"case when {MatchesSql(inner, path, alias, bind(filter))} then {inner} else {insert} end";
        });

        return this;
    }

    // ---- Remove ----

    /// <summary>
    ///     Remove the first, or every, element of an array member equal to <paramref name="element" />.
    /// </summary>
    /// <remarks>
    ///     Rebuilt rather than removed in place: json1 cannot address "the element equal to x" as a path.
    ///     <c>is</c> rather than <c>=</c>, because a JSON null element reads back as SQL NULL — the old
    ///     <c>&lt;&gt;</c> silently dropped every null in the array.
    /// </remarks>
    public IPatchExpression<T> Remove<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression,
        TElement element, RemoveAction action = RemoveAction.RemoveFirst)
    {
        var path = PathOf(expression);

        _operation.Add((inner, bind) =>
            RemoveSql(inner, path, "each_r", $"each_r.value is json_extract(json({bind(Json(element))}), '$')",
                action));

        return this;
    }

    public IPatchExpression<T> Remove<TElement>(Expression<Func<T, IDictionary<string, TElement>>> expression,
        string key)
        => DeletePath(KeyPath(PathOf(expression), key));

    public IPatchExpression<T> Remove<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression,
        Expression<Func<TElement, bool>> predicate, RemoveAction action = RemoveAction.RemoveFirst)
    {
        var (path, alias, filter) = ElementPredicate(expression, predicate, nameof(Remove));

        _operation.Add((inner, bind) => RemoveSql(inner, path, alias, bind(filter), action));

        return this;
    }

    /// <summary>
    ///     One element of a rebuilt array, as text that a surrounding <c>json(...)</c> re-parses.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Keyed on <c>json_each.type</c>, not on the value.</b> SQLite has no boolean, so a JSON
    ///         <c>true</c> arrives as the integer 1 and <c>json_quote</c> writes it back as <c>1</c> —
    ///         a rebuild that went through the value alone silently turned every boolean in the array
    ///         into a number. <c>type</c> is the only thing that still knows.
    ///     </para>
    ///     <para>
    ///         A JSON <c>null</c> needs nothing: its value is SQL NULL and <c>json_quote(NULL)</c> is
    ///         the text <c>null</c>. An object or array is already its own JSON text.
    ///     </para>
    ///     <para>
    ///         The result is deliberately plain text rather than a value carrying json1's JSON subtype,
    ///         because <b>the subtype does not survive a subquery</b> — <see cref="Insert{TElement}" />
    ///         projects this through one, and without the outer <c>json(...)</c> every element comes
    ///         back double-quoted. Verified against SQLite 3.51.
    ///     </para>
    /// </remarks>
    private const string ElementSql =
        "case type when 'true' then 'true' when 'false' then 'false' "
        + "when 'object' then value when 'array' then value else json_quote(value) end";

    public IPatchExpression<T> Rename(string oldName, Expression<Func<T, object>> expression)
    {
        var from = PathOfStoredKey(oldName);
        var to = PathOf(expression);

        _operation.Add((inner, _) =>
            $"json_remove(json_set({inner}, '{to}', json_extract({inner}, '{from}')), '{from}')");

        return this;
    }

    public IPatchExpression<T> Delete(string name) => DeletePath(PathOfStoredKey(name));

    public IPatchExpression<T> Delete<TParent>(string name, Expression<Func<T, TParent>> expression)
        => DeletePath(ChildPath(PathOf(expression), name));

    public IPatchExpression<T> Delete<TElement>(Expression<Func<T, TElement>> expression)
        => DeletePath(PathOf(expression));

    private IPatchExpression<T> AppendPath(string path, object? element)
    {
        // '[#]' is SQLite's append index, and json_insert creates the array when the member is absent.
        _operation.Add((inner, bind) => $"json_insert({inner}, '{path}[#]', json({bind(Json(element))}))");

        return this;
    }

    /// <summary>
    ///     Ordinals are doubled so the new element can sit strictly between two of them: an existing
    ///     element keeps 2k below the insertion point and takes 2k+2 at or above it, and the new one is
    ///     2*index+1. An index past the end lands above every existing ordinal, so it appends.
    /// </summary>
    private static string InsertAtSql(string inner, string path, int index, string parameter)
        => $"json_set({inner}, '{path}', (select json_group_array(json(v)) from ("
           + $"select case when key < {index} then key * 2 else key * 2 + 2 end as ord, "
           + $"{ElementSql} as v from json_each(json_extract({inner}, '{path}')) "
           + $"union all select {index} * 2 + 1, json({parameter}) order by ord)))";

    private static string ContainsValueSql(string inner, string path, string parameter)
        => $"exists(select 1 from json_each(json_extract({inner}, '{path}')) "
           + $"where value = json_extract(json({parameter}), '$'))";

    private static string MatchesSql(string inner, string path, string alias, string filter)
        => $"exists(select 1 from json_each(json_extract({inner}, '{path}')) as {alias} where {filter})";

    /// <summary>
    ///     Rebuild the array without the matching elements — every one, or only the one with the lowest
    ///     index.
    /// </summary>
    private static string RemoveSql(string inner, string path, string alias, string filter, RemoveAction action)
    {
        var source = $"json_each(json_extract({inner}, '{path}')) as {alias}";
        var keep = action == RemoveAction.RemoveAll
            ? $"not coalesce({filter}, 0)"
            : $"{alias}.key is not (select min({alias}.key) from {source} where {filter})";

        return $"json_set({inner}, '{path}', coalesce((select json_group_array(json({ElementSqlFor(alias)})) "
               + $"from {source} where {keep}), json_array()))";
    }

    private static string ElementSqlFor(string alias)
        => $"case {alias}.type when 'true' then 'true' when 'false' then 'false' "
           + $"when 'object' then {alias}.value when 'array' then {alias}.value else json_quote({alias}.value) end";

    /// <summary>
    ///     An element predicate, parsed by the same machinery a LINQ <c>Any(...)</c> over the member uses,
    ///     against the <c>json_each</c> alias that machinery expects.
    /// </summary>
    private (string Path, string Alias, Weasel.Core.SqlGeneration.ISqlFragment Filter) ElementPredicate<TElement>(
        Expression<Func<T, IEnumerable<TElement>>> expression, Expression<Func<TElement, bool>> predicate,
        string operatorName)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var path = PathOf(expression);
        var collection = ChildCollections.ResolveCollection(_members, MemberOf(expression), operatorName);
        var filter = ChildCollections.ParseElementPredicate(collection, predicate, operatorName);

        return (path, collection.Alias, filter);
    }

    private IPatchExpression<T> SetPath(string path, object? value)
    {
        _operation.Add((inner, bind) => $"json_set({inner}, '{path}', json({bind(Json(value))}))");

        return this;
    }

    private IPatchExpression<T> IncrementPath(string path, object increment)
    {
        // coalesce, because json_extract of an absent key is NULL and NULL + 1 is NULL — the member
        // would silently become null rather than the increment.
        _operation.Add((inner, bind) =>
            $"json_set({inner}, '{path}', coalesce(json_extract({inner}, '{path}'), 0) + {bind(increment)})");

        return this;
    }

    private IPatchExpression<T> DeletePath(string path)
    {
        _operation.Add((inner, _) => $"json_remove({inner}, '{path}')");

        return this;
    }

    /// <summary>
    ///     The value as JSON, through the store's own serializer.
    /// </summary>
    /// <remarks>
    ///     <b>Not through <see cref="SqliteParameterValue" />, which is fisher#34's conversion for
    ///     comparing against columns.</b> A patched value lands inside <c>data</c>, so it has to match
    ///     what a full write would have produced — a timestamp in System.Text.Json's format rather than
    ///     <c>SqliteTimestamp</c>'s, a Guid however the serializer renders it. Wrapping in
    ///     <c>json(?)</c> then makes a string a JSON string, a number a JSON number and an object a JSON
    ///     object, with no per-type branching.
    /// </remarks>
    private string Json(object? value) => _serializer.ToJson(value);

    /// <summary>
    ///     The JSON path for a member, taken from the same <see cref="MemberFactory" /> a query uses.
    /// </summary>
    /// <remarks>
    ///     Taken from the resolved member rather than built from the CLR name, so a patch addresses
    ///     exactly what a predicate addresses — including <c>[JsonPropertyName]</c> and the serializer's
    ///     naming policy. The same rule fisher#16 established for indexes.
    /// </remarks>
    private string PathOf(LambdaExpression member)
        => PathFromLocator(_members.ResolveMember(MemberOf(member)).RawLocator);

    private static MemberExpression MemberOf(LambdaExpression member)
    {
        var body = member.Body;

        while (body is UnaryExpression { NodeType: ExpressionType.Convert } convert)
        {
            body = convert.Operand;
        }

        return body as MemberExpression
               ?? throw new JasperFx.BadLinqExpressionException(
                   "A patch target must be a document member — for example x => x.Name.");
    }

    /// <summary>
    ///     A stored key under a resolved parent path, for the overloads that name a key relative to a
    ///     member.
    /// </summary>
    private static string ChildPath(string parentPath, string name)
        => parentPath + PathOfStoredKey(name)[1..];

    /// <summary>
    ///     One dictionary entry's path. The key is quoted, so it may hold a dot or a space; a quote
    ///     character of either kind is refused, since it would close the quoting or the string literal
    ///     the path is written into.
    /// </summary>
    private static string KeyPath(string dictionaryPath, string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return key.Contains('"') || key.Contains('\'') || key.Contains('\\')
            ? throw new ArgumentException("A dictionary key in a patch cannot contain a quote or a backslash.",
                nameof(key))
            : $"{dictionaryPath}.\"{key}\"";
    }

    /// <summary>
    ///     A path built from the stored key directly, with no member to resolve.
    /// </summary>
    /// <remarks>
    ///     Deliberately not routed through <see cref="MemberFactory" />: the whole reason the by-name
    ///     overloads exist is to address a key the type has no member for — a renamed one, or one left
    ///     behind by an older shape of the document. Resolving it would refuse exactly the case it is
    ///     for.
    /// </remarks>
    private static string PathOfStoredKey(string storedKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedKey);

        // A quote would let a caller close the string literal the path is written into.
        return storedKey.Contains('\'')
            ? throw new ArgumentException("A stored key cannot contain a quote.", nameof(storedKey))
            : $"$.{storedKey}";
    }

    /// <summary>
    ///     The <c>$.path</c> out of a <c>json_extract(data, '$.path')</c> locator.
    /// </summary>
    /// <remarks>
    ///     Reading it back off the locator keeps one source of truth for the path. A member with a
    ///     duplicated column resolves to the column name instead, which is not a path — hence the
    ///     explicit check rather than a silent malformed patch.
    /// </remarks>
    private static string PathFromLocator(string locator)
    {
        const string prefix = "json_extract(data, '";

        return locator.StartsWith(prefix, StringComparison.Ordinal) && locator.EndsWith("')", StringComparison.Ordinal)
            ? locator[prefix.Length..^2]
            : throw new JasperFx.BadLinqExpressionException(
                $"Fisher cannot patch through the locator '{locator}' — a patch addresses a JSON path, "
                + "and this member does not resolve to one.");
    }
}
