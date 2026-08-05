using System.Linq.Expressions;
using Fisher.Linq.Members;
using Fisher.Linq.SqlGeneration;

namespace Fisher.Linq.Parsing.Methods;

/// <summary>
///     Translates one method call into a SQL fragment.
/// </summary>
internal interface IMethodCallParser
{
    bool Matches(MethodCallExpression expression);
    ISqlFragment Parse(IMemberResolver memberFactory, MethodCallExpression expression);
}
