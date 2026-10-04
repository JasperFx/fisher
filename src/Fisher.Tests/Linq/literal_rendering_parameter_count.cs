using Fisher.Linq.SqlGeneration;
using Weasel.Core;

namespace Fisher.Tests.Linq;

/// <summary>
///     fisher#389 — the literal-rendering builder answers <see cref="ICommandBuilder.ParameterCount" />
///     with a definite zero, not the interface's "unknown" default.
/// </summary>
/// <remarks>
///     Asserted through the interface, because that is where a caller sizing a value list reads it, and
///     where an implementation that forgot the member would silently answer
///     <see cref="ICommandBuilder.UnknownParameterCount" /> instead.
/// </remarks>
public class literal_rendering_parameter_count
{
    [Fact]
    public void values_rendered_as_literals_spend_none_of_the_parameter_budget()
    {
        ICommandBuilder builder = new LiteralRenderingCommandBuilder();

        builder.Append("x = ");
        builder.AppendParameter(42);
        builder.Append(" and y = ");
        builder.AppendParameter("text");

        builder.ParameterCount.ShouldBe(0);
        builder.ParameterCount.ShouldNotBe(ICommandBuilder.UnknownParameterCount);
        builder.ToString().ShouldContain("42");
    }
}
