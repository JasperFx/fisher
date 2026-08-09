using System.Linq.Expressions;
using Fisher.Linq.Members;
using Fisher.Storage;

namespace Fisher.Patching;

/// <summary>
///     Changing part of a stored document without loading it (fisher#35).
/// </summary>
/// <remarks>
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
/// </remarks>
public interface IPatchExpression<T>
{
    /// <summary>Set a member to a value, creating the path if it is absent.</summary>
    IPatchExpression<T> Set<TValue>(Expression<Func<T, TValue>> member, TValue value);

    /// <summary>
    ///     Set a value at a stored JSON key, named as it appears in <c>data</c>.
    /// </summary>
    /// <remarks>
    ///     The string is the <em>stored</em> key — <c>"name"</c>, not <c>"Name"</c>, under the default
    ///     camelCase policy — because the point of the by-name overloads is reaching keys the type no
    ///     longer has a member for. The lambda overload is the one that resolves through the naming
    ///     policy for you.
    /// </remarks>
    IPatchExpression<T> Set<TValue>(string storedKey, TValue value);

    /// <summary>Copy one member's new value into several places at once.</summary>
    IPatchExpression<T> Duplicate<TValue>(Expression<Func<T, TValue>> source, TValue value,
        params Expression<Func<T, TValue>>[] destinations);

    /// <summary>
    ///     Add to a numeric member. An absent member counts as zero rather than staying absent.
    /// </summary>
    IPatchExpression<T> Increment(Expression<Func<T, int>> member, int increment = 1);

    /// <inheritdoc cref="Increment(Expression{Func{T,int}},int)" />
    IPatchExpression<T> Increment(Expression<Func<T, long>> member, long increment = 1);

    /// <inheritdoc cref="Increment(Expression{Func{T,int}},int)" />
    IPatchExpression<T> Increment(Expression<Func<T, double>> member, double increment = 1);

    /// <inheritdoc cref="Increment(Expression{Func{T,int}},int)" />
    IPatchExpression<T> Increment(Expression<Func<T, decimal>> member, decimal increment = 1);

    /// <inheritdoc cref="Increment(Expression{Func{T,int}},int)" />
    /// <remarks>
    ///     The nullable overloads exist because a null or absent member is the case the operation has
    ///     to get right — <c>json_extract</c> returns SQL NULL for both, and <c>NULL + n</c> is NULL.
    ///     It counts as zero.
    /// </remarks>
    IPatchExpression<T> Increment(Expression<Func<T, int?>> member, int increment = 1);

    /// <inheritdoc cref="Increment(Expression{Func{T,int?}},int)" />
    IPatchExpression<T> Increment(Expression<Func<T, long?>> member, long increment = 1);

    /// <inheritdoc cref="Increment(Expression{Func{T,int?}},int)" />
    IPatchExpression<T> Increment(Expression<Func<T, double?>> member, double increment = 1);

    /// <inheritdoc cref="Increment(Expression{Func{T,int?}},int)" />
    IPatchExpression<T> Increment(Expression<Func<T, decimal?>> member, decimal increment = 1);

    /// <summary>
    ///     Append to an array member. An absent member becomes a one-element array.
    /// </summary>
    IPatchExpression<T> Append<TElement>(Expression<Func<T, IEnumerable<TElement>>> member, TElement value);

    /// <inheritdoc cref="Append{TElement}" />
    IPatchExpression<T> AppendIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> member,
        TElement value);

    /// <summary>
    ///     Insert into an array member at <paramref name="index" />, shifting the rest along
    ///     (fisher#52).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b><c>json_insert</c> is not the mechanism, which is why this arrived after the rest.</b>
    ///         It only inserts where the path does not already exist, so at an occupied index it is a
    ///         silent no-op — <c>json_insert('{"t":["a","b"]}', '$.t[1]', json('"z"'))</c> returns the
    ///         document unchanged. <c>json_replace</c> would overwrite rather than shift, which is a
    ///         different operation. So the array is rebuilt, the way <see cref="Remove{TElement}" />
    ///         rebuilds it, with the new element placed by ordinal.
    ///     </para>
    ///     <para>
    ///         <b>An index past the end appends rather than throwing.</b> A <c>List&lt;T&gt;</c> would
    ///         throw, but a patch does not read the document — refusing would mean a round trip to learn
    ///         a length, which is the whole cost patching exists to avoid. A negative index is refused,
    ///         because it names no position at all.
    ///     </para>
    ///     <para>
    ///         An absent or empty member becomes a one-element array, as <see cref="Append{TElement}" />
    ///         does.
    ///     </para>
    /// </remarks>
    IPatchExpression<T> Insert<TElement>(Expression<Func<T, IEnumerable<TElement>>> member,
        TElement value, int index = 0);

    /// <summary>
    ///     Remove every element of an array member equal to <paramref name="value" />.
    /// </summary>
    /// <remarks>
    ///     Every match, not the first. json1 has no way to address "the first element equal to x" for
    ///     removal, so the array is rebuilt without the matching elements — and dropping only one
    ///     occurrence would mean deciding which, which the rebuild has no way to express.
    /// </remarks>
    IPatchExpression<T> Remove<TElement>(Expression<Func<T, IEnumerable<TElement>>> member, TElement value);

    /// <summary>Rename a member, keeping its value.</summary>
    /// <remarks>The renamed key moves to the end of the object — json1 cannot rename in place.</remarks>
    IPatchExpression<T> Rename(string oldName, Expression<Func<T, object>> newMember);

    /// <summary>Remove a member entirely.</summary>
    IPatchExpression<T> Delete<TValue>(Expression<Func<T, TValue>> member);

    /// <inheritdoc cref="Set{TValue}(string,TValue)" />
    IPatchExpression<T> Delete(string storedKey);
}

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

    public IPatchExpression<T> Set<TValue>(Expression<Func<T, TValue>> member, TValue value)
        => SetPath(PathOf(member), value);

    public IPatchExpression<T> Set<TValue>(string storedKey, TValue value)
        => SetPath(PathOfStoredKey(storedKey), value);

    public IPatchExpression<T> Duplicate<TValue>(Expression<Func<T, TValue>> source, TValue value,
        params Expression<Func<T, TValue>>[] destinations)
    {
        SetPath(PathOf(source), value);

        foreach (var destination in destinations)
        {
            SetPath(PathOf(destination), value);
        }

        return this;
    }

    public IPatchExpression<T> Increment(Expression<Func<T, int>> member, int increment = 1)
        => IncrementPath(PathOf(member), increment);

    public IPatchExpression<T> Increment(Expression<Func<T, long>> member, long increment = 1)
        => IncrementPath(PathOf(member), increment);

    public IPatchExpression<T> Increment(Expression<Func<T, double>> member, double increment = 1)
        => IncrementPath(PathOf(member), increment);

    public IPatchExpression<T> Increment(Expression<Func<T, decimal>> member, decimal increment = 1)
        => IncrementPath(PathOf(member), (double)increment);

    public IPatchExpression<T> Increment(Expression<Func<T, int?>> member, int increment = 1)
        => IncrementPath(PathOf(member), increment);

    public IPatchExpression<T> Increment(Expression<Func<T, long?>> member, long increment = 1)
        => IncrementPath(PathOf(member), increment);

    public IPatchExpression<T> Increment(Expression<Func<T, double?>> member, double increment = 1)
        => IncrementPath(PathOf(member), increment);

    public IPatchExpression<T> Increment(Expression<Func<T, decimal?>> member, decimal increment = 1)
        => IncrementPath(PathOf(member), (double)increment);

    public IPatchExpression<T> Append<TElement>(Expression<Func<T, IEnumerable<TElement>>> member,
        TElement value)
    {
        var path = PathOf(member);

        // '[#]' is SQLite's append index, and json_insert creates the array when the member is absent.
        _operation.Add((inner, bind) =>
            $"json_insert({inner}, '{path}[#]', json({bind(Json(value))}))");

        return this;
    }

    public IPatchExpression<T> AppendIfNotExists<TElement>(
        Expression<Func<T, IEnumerable<TElement>>> member, TElement value)
    {
        var path = PathOf(member);

        _operation.Add((inner, bind) =>
        {
            var parameter = bind(Json(value));

            return $"case when exists(select 1 from json_each(json_extract({inner}, '{path}')) "
                   + $"where value = json_extract(json({parameter}), '$')) then {inner} "
                   + $"else json_insert({inner}, '{path}[#]', json({parameter})) end";
        });

        return this;
    }

    public IPatchExpression<T> Insert<TElement>(Expression<Func<T, IEnumerable<TElement>>> member,
        TElement value, int index = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        var path = PathOf(member);

        // Ordinals are doubled so the new element can sit strictly between two of them: an existing
        // element keeps 2k below the insertion point and takes 2k+2 at or above it, and the new one is
        // 2*index+1. An index past the end simply lands above every existing ordinal, which is why
        // that case appends with no length to check.
        _operation.Add((inner, bind) =>
            $"json_set({inner}, '{path}', (select json_group_array(json(v)) from ("
            + $"select case when key < {index} then key * 2 else key * 2 + 2 end as ord, "
            + $"{ElementSql} as v from json_each(json_extract({inner}, '{path}')) "
            + $"union all select {index} * 2 + 1, json({bind(Json(value))}) order by ord)))");

        return this;
    }

    public IPatchExpression<T> Remove<TElement>(Expression<Func<T, IEnumerable<TElement>>> member,
        TElement value)
    {
        var path = PathOf(member);

        // Rebuilt rather than removed in place: json1 cannot address "the element equal to x" as a
        // path.
        //
        // `is not` rather than `<>`, because a JSON null element reads back as SQL NULL and `NULL <> x`
        // is NULL rather than true — so every removal silently dropped every null in the array, and
        // Remove(member, null) removed nothing.
        _operation.Add((inner, bind) =>
            $"json_set({inner}, '{path}', coalesce((select json_group_array(json({ElementSql})) "
            + $"from json_each(json_extract({inner}, '{path}')) "
            + $"where value is not json_extract(json({bind(Json(value))}), '$')), json_array()))");

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

    public IPatchExpression<T> Rename(string oldName, Expression<Func<T, object>> newMember)
    {
        var from = PathOfStoredKey(oldName);
        var to = PathOf(newMember);

        _operation.Add((inner, _) =>
            $"json_remove(json_set({inner}, '{to}', json_extract({inner}, '{from}')), '{from}')");

        return this;
    }

    public IPatchExpression<T> Delete<TValue>(Expression<Func<T, TValue>> member)
        => DeletePath(PathOf(member));

    public IPatchExpression<T> Delete(string storedKey) => DeletePath(PathOfStoredKey(storedKey));

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
    {
        var body = member.Body;

        while (body is UnaryExpression { NodeType: ExpressionType.Convert } convert)
        {
            body = convert.Operand;
        }

        if (body is not MemberExpression memberExpression)
        {
            throw new Linq.BadLinqExpressionException(
                "A patch target must be a document member — for example x => x.Name.");
        }

        return PathFromLocator(_members.ResolveMember(memberExpression).RawLocator);
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
            : throw new Linq.BadLinqExpressionException(
                $"Fisher cannot patch through the locator '{locator}' — a patch addresses a JSON path, "
                + "and this member does not resolve to one.");
    }
}
