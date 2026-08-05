using System.Linq.Expressions;

namespace Fisher.Linq.Members;

/// <summary>
///     Resolves a member expression to the <see cref="IQueryableMember" /> that knows its SQL locator.
/// </summary>
internal interface IMemberResolver
{
    IQueryableMember ResolveMember(MemberExpression expression);
}
