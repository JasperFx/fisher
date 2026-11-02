using System.Linq.Expressions;
using JasperFx.Events;

namespace Fisher.Events.Archiving;

/// <summary>
///     Whether a predicate over <see cref="IEvent" /> has taken the archival question away from the
///     implicit <c>is_archived = 0</c> filter.
/// </summary>
/// <remarks>
///     <para>
///         Marten decides this from its SQL fragments, with an <c>IArchiveFilter</c> marker. Fisher's
///         <c>IsArchived</c> is an ordinary column to the <see cref="Linq.Parsing.WhereClauseParser" />,
///         so the question is put to the expression instead, and the answer is the same: naming
///         <see cref="IEvent.IsArchived" /> or calling
///         <see cref="ArchivedEventExtensions.MaybeArchived" /> anywhere in the predicate opts in,
///         including on one side of an <c>||</c>.
///     </para>
///     <para>
///         Only the event being filtered counts, which means the predicate's own parameter. An event body
///         with an <c>IsArchived</c> of its own says nothing about the row. Neither does the flag of an
///         event captured from outside the query, or a nested lambda over events held in memory.
///     </para>
/// </remarks>
internal static class ArchivalPredicates
{
    public static bool SpecifiesEventArchivalStatus(this LambdaExpression predicate)
    {
        var row = predicate.Parameters[0];

        if (!typeof(IEvent).IsAssignableFrom(row.Type))
        {
            return false;
        }

        var finder = new ArchivalStatusFinder(row);
        finder.Visit(predicate.Body);

        return finder.Found;
    }

    private sealed class ArchivalStatusFinder(ParameterExpression row) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Member.Name == nameof(IEvent.IsArchived) && IsTheRow(node.Expression))
            {
                Found = true;
            }

            return base.VisitMember(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(ArchivedEventExtensions) && IsTheRow(node.Arguments[0]))
            {
                Found = true;
            }

            return base.VisitMethodCall(node);
        }

        private bool IsTheRow(Expression? expression)
        {
            while (expression is UnaryExpression { NodeType: ExpressionType.Convert } convert)
            {
                expression = convert.Operand;
            }

            return expression == row;
        }
    }
}
