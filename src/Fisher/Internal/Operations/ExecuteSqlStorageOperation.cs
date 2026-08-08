using System.Data.Common;
using Fisher.Storage;
using Weasel.Core;
using Weasel.Storage;

namespace Fisher.Internal.Operations;

/// <summary>
///     Arbitrary SQL enrolled in the session's unit of work — what
///     <c>IDocumentSession.QueueSqlCommand</c> queues (fisher#34).
/// </summary>
/// <remarks>
///     <para>
///         <b>This matters more on SQLite than on either sibling.</b> An application using Fisher keeps
///         its own tables in the same file, and SQLite permits one writer per file — so without a way
///         to enrol its own statements in Fisher's transaction, an application must either give up
///         atomicity or take the write lock twice and contend with itself. Marten and Polecat have the
///         same method for convenience; here it is the difference between one transaction and two
///         writers on one file.
///     </para>
///     <para>
///         Ported from Polecat's <c>ExecuteSqlStorageOperation</c>, with one addition it does not need:
///         parameter values go through <see cref="SqliteParameterValue" /> so a Guid, a timestamp or a
///         decimal matches what Fisher actually stored. See that type for why each of the three would
///         otherwise fail silently.
///     </para>
///     <para>
///         <see cref="DocumentType" /> is <see cref="object" /> deliberately: the session's
///         <c>EnsureDocumentTablesAsync</c> asks the schema whether each operation's type is mapped, and
///         a type that is not mapped is skipped. Queued SQL names its own tables and Fisher must not
///         try to create one for it.
///     </para>
/// </remarks>
internal sealed class ExecuteSqlStorageOperation : Weasel.Storage.IStorageOperation
{
    private readonly string _commandText;
    private readonly char _placeholder;
    private readonly object?[] _parameterValues;

    internal ExecuteSqlStorageOperation(string commandText, char placeholder, object?[] parameterValues)
    {
        _commandText = commandText.TrimEnd(';');
        _placeholder = placeholder;
        _parameterValues = parameterValues;
    }

    public Type DocumentType => typeof(object);

    /// <remarks>
    ///     <see cref="OperationRole.Other" />, where Polecat says <c>Upsert</c>. Neither store orders
    ///     its unit of work by role — Fisher executes in queue order, which is what a caller writing
    ///     "insert my row, then append the event" assumes — so the honest value costs nothing.
    /// </remarks>
    public OperationRole Role() => OperationRole.Other;

    public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
    {
        var parameters = builder.AppendWithDbParameters(_commandText, _placeholder);

        if (parameters.Length != _parameterValues.Length)
        {
            throw new InvalidOperationException(
                $"Wrong number of parameter values for queued SQL '{_commandText}': the statement has "
                + $"{parameters.Length} '{_placeholder}' placeholders and {_parameterValues.Length} "
                + "values were supplied. If the SQL contains a literal "
                + $"'{_placeholder}', use the overload that takes a different placeholder character.");
        }

        // Weasel stamps every placeholder parameter TEXT before the real value is known. That is left
        // alone on purpose: Microsoft.Data.Sqlite binds by the CLR type of Value and ignores the
        // declared SqliteType, verified against 10.0.9 — an int in a TEXT-stamped parameter still
        // reports `typeof() = integer`. Resetting it would be a no-op that reads as load-bearing.
        for (var i = 0; i < parameters.Length; i++)
        {
            parameters[i].Value = SqliteParameterValue.ToDatabaseValue(_parameterValues[i]);
        }
    }

    public Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
        => Task.CompletedTask;
}
