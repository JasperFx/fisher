using System.Data.Common;
using Microsoft.Data.Sqlite;
using Weasel.Core;

namespace Fisher.Storage;

/// <summary>
///     A <see cref="Weasel.Sqlite.CommandBuilder" /> that also satisfies the dialect-neutral
///     <see cref="ICommandBuilder" />, which is the surface every shared closed-shape storage
///     operation configures itself against.
/// </summary>
/// <remarks>
///     <para>
///         This exists to paper over a gap in Weasel.Sqlite 9.23.1: <c>Weasel.SqlServer.CommandBuilder</c>
///         declares <c>Weasel.Core.ICommandBuilder</c> and supplies the three members
///         <c>CommandBuilderBase</c> cannot — <see cref="AppendParameter(object)" /> (the base returns
///         void where the interface returns the created <see cref="DbParameter" />),
///         <see cref="AppendParameters" />, and <see cref="CreateGroupedParameterBuilder" /> — but
///         <c>Weasel.Sqlite.CommandBuilder</c> declares neither the interface nor those members.
///         Without this shim no Weasel.Storage operation can be configured against a SQLite command
///         builder at all.
///     </para>
///     <para>
///         This belongs upstream in Weasel.Sqlite rather than here; it is a faithful port of the
///         SQL Server implementation and should be deleted once Weasel.Sqlite carries it.
///     </para>
/// </remarks>
internal sealed class FisherCommandBuilder : Weasel.Sqlite.CommandBuilder, ICommandBuilder
{
    public FisherCommandBuilder() : this(new SqliteCommand())
    {
    }

    public FisherCommandBuilder(SqliteCommand command) : base(command)
    {
    }

    /// <summary>
    ///     The tenant this command is being built for. Part of <see cref="ICommandBuilder" /> because
    ///     conjoined-tenancy SQL generation reaches for it often enough that Weasel hangs it here.
    /// </summary>
    public string TenantId { get; set; } = JasperFx.StorageConstants.DefaultTenantId;

    /// <summary>
    ///     Append the supplied parameter values to the command, comma-separated.
    /// </summary>
    public void AppendParameters(params object[] parameters)
    {
        if (parameters.Length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters),
                "Must be at least one parameter value, but got " + parameters.Length);
        }

        AppendParameter(parameters[0]);

        for (var i = 1; i < parameters.Length; i++)
        {
            Append(", ");
            AppendParameter(parameters[i]);
        }
    }

    /// <summary>
    ///     Append a single parameter through the dialect-neutral value path, returning the newly
    ///     created parameter upcast to <see cref="DbParameter" />.
    /// </summary>
    public new DbParameter AppendParameter(object value)
    {
        base.AppendParameter(value);
        return _command.Parameters[^1];
    }

    public IGroupedParameterBuilder CreateGroupedParameterBuilder(char? separator = null)
        => new GroupedParameterBuilder(this, separator);

    /// <summary>
    ///     A no-op, as in the SQL Server builder: Microsoft.Data.Sqlite executes several
    ///     semicolon-separated statements from a single command.
    /// </summary>
    public override void StartNewCommand()
    {
    }
}
