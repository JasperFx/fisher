namespace Fisher.Linq;

/// <summary>
///     Thrown when a LINQ expression cannot be translated to SQL that Fisher can answer correctly.
/// </summary>
/// <remarks>
///     <para>
///         Mirrors Polecat's and Marten's exception of the same name. Fisher raises it in one place the
///         siblings do not: an ordering or range comparison over a date member, whose stored JSON form is
///         not sortable as text. Throwing there is the point — the alternative is a query that returns
///         plausible but wrong rows.
///     </para>
///     <para>
///         <b>Derives from <see cref="JasperFx.BadLinqExpressionException" /> as of JasperFx 2.71.0
///         (jasperfx#795), which lifted the three per-store copies.</b> Kept as a Fisher type rather
///         than deleted in favour of the shared one, so that application code already catching
///         <c>Fisher.Linq.BadLinqExpressionException</c> keeps compiling and keeps catching; code that
///         wants to be store-agnostic catches the base instead. The derived type adds nothing — it
///         exists for the name.
///     </para>
///     <para>
///         ⚠️ Both names are now in scope wherever a file imports <c>Fisher.Linq</c> and
///         <c>JasperFx</c>, which is CS0104 rather than a silent bind. Fisher.Tests settles it once
///         with a global alias; see <c>src/Fisher.Tests/BadLinqExpressionAlias.cs</c>.
///     </para>
/// </remarks>
public class BadLinqExpressionException : JasperFx.BadLinqExpressionException
{
    public BadLinqExpressionException(string message) : base(message)
    {
    }

    public BadLinqExpressionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
