namespace Fisher.Linq;

/// <summary>
///     Thrown when a LINQ expression cannot be translated to SQL that Fisher can answer correctly.
/// </summary>
/// <remarks>
///     Mirrors Polecat's and Marten's exception of the same name. Fisher raises it in one place the
///     siblings do not: an ordering or range comparison over a date member, whose stored JSON form is
///     not sortable as text. Throwing there is the point — the alternative is a query that returns
///     plausible but wrong rows.
/// </remarks>
public class BadLinqExpressionException : Exception
{
    public BadLinqExpressionException(string message) : base(message)
    {
    }

    public BadLinqExpressionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
