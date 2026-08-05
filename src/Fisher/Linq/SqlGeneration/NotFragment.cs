using Weasel.Core;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     Negates an inner fragment — <c>not (inner)</c>.
/// </summary>
internal class NotFragment : ISqlFragment
{
    private readonly ISqlFragment _inner;

    public NotFragment(ISqlFragment inner)
    {
        _inner = inner;
    }

    public void Apply(ICommandBuilder builder)
    {
        builder.Append("not (");
        _inner.Apply(builder);
        builder.Append(')');
    }
}
