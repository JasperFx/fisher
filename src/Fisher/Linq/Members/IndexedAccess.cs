using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Fisher.Linq.Parsing;

namespace Fisher.Linq.Members;

/// <summary>
///     An index into an array or a list on the document: <c>x.Values[1]</c>, <c>x.Items[0].Name</c>,
///     <c>x.Grid[1][0]</c>, <c>x.Values.ElementAt(2)</c> (fisher#666, the port of marten#5949).
/// </summary>
/// <remarks>
///     <para>
///         An index is one more step of the JSON path: <c>$.values[1]</c>, <c>$.items[0].name</c>,
///         <c>$.grid[1][0]</c>. So it resolves through <see cref="MemberFactory" /> like any other member and
///         is typed by the element's CLR type, which is what makes an enum, a timestamp or a decimal element
///         compare and order the way the same type does anywhere else.
///     </para>
///     <para>
///         <b>An index past the end reads as missing</b>, because <c>json_extract</c> yields SQL NULL for a
///         path that is not there. So the element matches no comparison and sorts as a null, where LINQ to
///         Objects would throw. A missing or null array reads the same way.
///     </para>
///     <para>
///         The index has to be a value: a literal, a captured local or a static. A negative one is refused,
///         because C# throws on it and SQLite's path syntax has no negative index. One computed from the row
///         is refused too, because the path is part of the SQL text.
///     </para>
/// </remarks>
internal static class IndexedAccess
{
    /// <summary>
    ///     Whether <paramref name="node" /> indexes a collection, and if so what it indexes and by what.
    /// </summary>
    public static bool TryMatch(Expression node, out Expression collection, out Expression index)
    {
        switch (node)
        {
            // An array's indexer is its own node, not a method call.
            case BinaryExpression { NodeType: ExpressionType.ArrayIndex } array
                when array.Right.Type == typeof(int):
                collection = array.Left;
                index = array.Right;
                return true;

            // A list's int indexer. A dictionary's indexer is get_Item too, so the owner has to be a list.
            case MethodCallExpression { Method.Name: "get_Item", Object: { } owner, Arguments.Count: 1 } call
                when call.Arguments[0].Type == typeof(int) && IsList(owner.Type):
                collection = owner;
                index = call.Arguments[0];
                return true;

            case MethodCallExpression { Method.Name: nameof(Enumerable.ElementAt), Object: null, Arguments.Count: 2 } call
                when call.Method.DeclaringType == typeof(Enumerable) && call.Arguments[1].Type == typeof(int):
                collection = call.Arguments[0];
                index = call.Arguments[1];
                return true;
        }

        collection = null!;
        index = null!;
        return false;
    }

    /// <summary>Whether <paramref name="node" /> indexes a collection.</summary>
    public static bool IsIndex(Expression node) => TryMatch(node, out _, out _);

    /// <summary>
    ///     The next step towards the lambda's parameter: a member's owner (through a union case's cast), an
    ///     index's collection, or null when the chain ends here.
    /// </summary>
    public static Expression? ParentOf(Expression node) => node switch
    {
        MemberExpression member => UnionMemberAccess.ParentOf(member),
        _ when TryMatch(node, out var collection, out _) => collection,
        _ => null
    };

    /// <summary>
    ///     The parameter a chain of members and indexes starts at, or null when it starts anywhere else.
    /// </summary>
    public static ParameterExpression? RootOf(Expression node)
    {
        Expression? current = node;

        while (current is not null and not ParameterExpression)
        {
            current = ParentOf(current);
        }

        return current as ParameterExpression;
    }

    /// <summary>Whether a member chain indexes a collection anywhere along it.</summary>
    public static bool ContainsIndex(Expression node)
    {
        for (Expression? current = node; current is not null; current = ParentOf(current))
        {
            if (IsIndex(current))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The value of an index, refusing one that reads the row or is negative.
    /// </summary>
    /// <param name="index">The index expression.</param>
    /// <param name="whole">The indexing expression, named in a refusal.</param>
    public static int ValueOf(Expression index, Expression whole)
    {
        if (WhereClauseParser.ReferencesFreeParameter(index))
        {
            throw new BadLinqExpressionException(
                $"Cannot translate '{whole}': the index '{index}' is computed from the document. An index into a "
                + "document's array becomes part of the JSON path, so it has to be a value: a literal, a captured "
                + "variable or a static.");
        }

        var value = (int)WhereClauseParser.ExtractValue(index)!;

        if (value < 0)
        {
            throw new BadLinqExpressionException(
                $"Cannot translate '{whole}': the index {value} is negative. C# throws on it, and SQLite's JSON "
                + "path has no negative index.");
        }

        return value;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070:DynamicallyAccessedMembers",
        Justification = "Same classification reflection as CollectionMember.TryGetElementType; see its justification.")]
    private static bool IsList(Type type)
        => (type.IsGenericType && IsListDefinition(type.GetGenericTypeDefinition()))
           || type.GetInterfaces().Any(x => x.IsGenericType && IsListDefinition(x.GetGenericTypeDefinition()));

    private static bool IsListDefinition(Type definition)
        => definition == typeof(IList<>) || definition == typeof(IReadOnlyList<>) || definition == typeof(List<>);
}
