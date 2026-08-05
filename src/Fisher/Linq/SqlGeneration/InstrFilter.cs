using Weasel.Core;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     A substring test built on SQLite's <c>instr</c> — <c>instr(locator, @p0) &gt; 0</c>.
/// </summary>
/// <remarks>
///     The needle is bound as a parameter while the comparison operand is a literal integer, because
///     the latter is always a translator-chosen 0 or 1 and never caller data. See
///     <see cref="Parsing.Methods.StringMethods" /> for why this exists instead of a <c>LIKE</c>
///     pattern.
/// </remarks>
internal class InstrFilter : ISqlFragment
{
    private readonly string _locator;
    private readonly string _needle;
    private readonly string _op;
    private readonly int _operand;

    public InstrFilter(string locator, string needle, string op, int operand)
    {
        _locator = locator;
        _needle = needle;
        _op = op;
        _operand = operand;
    }

    public void Apply(ICommandBuilder builder)
    {
        builder.Append("instr(");
        builder.Append(_locator);
        builder.Append(", ");
        builder.AppendParameter(_needle);
        builder.Append(") ");
        builder.Append(_op);
        builder.Append(' ');
        builder.Append(_operand.ToString());
    }
}
