using System.Data.Common;

namespace Fisher;

/// <summary>
///     Writes every statement a session runs to the console. The "just show me what it is doing"
///     logger, for a test or a spike.
/// </summary>
/// <remarks>
///     <para>
///         Mirrors Marten's <c>ConsoleMartenLogger</c>, with the same parameter-value default as
///         <see cref="DefaultFisherLogger" /> and for the same reasons — see
///         <see cref="DefaultFisherLogger.LogParameterValues" />. Pass <c>true</c> to see the values.
///     </para>
///     <para>
///         <c>Console.Out</c> is resolved per write rather than captured, so a test harness that
///         redirects it after building the store is still obeyed.
///     </para>
/// </remarks>
public sealed class ConsoleFisherLogger: IFisherLogger, IFisherSessionLogger
{
    private readonly bool _logParameterValues;

    public ConsoleFisherLogger(bool logParameterValues = false)
        => _logParameterValues = logParameterValues;

    public IFisherSessionLogger StartSession(IQuerySession session) => this;

    public void OnBeforeExecute(DbCommand command)
    {
    }

    public void LogSuccess(DbCommand command) => Write(command);

    public void LogFailure(DbCommand command, Exception ex)
    {
        Console.Out.WriteLine("Fisher command failed!");
        Write(command);
        Console.Out.WriteLine(ex);
    }

    public void LogFailure(Exception ex, string message)
    {
        Console.Out.WriteLine("Failure: " + message);
        Console.Out.WriteLine(ex);
    }

    public void RecordSavedChanges(IDocumentSession session, Services.IChangeSet commit)
    {
        var counts = DefaultFisherLogger.CommitCounts.For(commit);

        Console.Out.WriteLine(
            $"Persisted {counts.Operations} operations — {counts.Updated} updates, {counts.Inserted} "
            + $"inserts, {counts.Deleted} deletions, {counts.Events} events across {counts.Streams} streams");
    }

    private void Write(DbCommand command)
    {
        Console.Out.WriteLine(command.CommandText);

        var parameters = DefaultFisherLogger.Describe(command, _logParameterValues);

        if (parameters.Length > 0)
        {
            Console.Out.WriteLine(parameters);
        }
    }
}
