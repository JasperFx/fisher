using Weasel.Core;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     A fragment of SQL that knows how to write itself onto a command builder.
/// </summary>
/// <remarks>
///     Mirrors <c>Polecat.Linq.SqlGeneration.ISqlFragment</c>. Both siblings own this namespace
///     themselves rather than taking it from their Weasel dialect package — <c>Weasel.Core</c> declares
///     only the marker, and <c>Weasel.Sqlite</c> has no <c>SqlGeneration</c> namespace at all — so
///     Fisher carrying its own is the mirror, not a divergence.
///     <para>
///         The builder is <see cref="Weasel.Core.ICommandBuilder" />, not a SQLite-specific type. That
///         is the interface weasel#424 taught <c>Weasel.Sqlite.CommandBuilder</c> to declare; see the
///         closed-upstream-gap note in CLAUDE.md.
///     </para>
/// </remarks>
internal interface ISqlFragment
{
    void Apply(ICommandBuilder builder);
}
