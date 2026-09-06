using System.Data.Common;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fisher;

/// <summary>
///     The <see cref="IFisherLogger" /> <c>AddFisher</c> attaches: Fisher's SQL, through the host's
///     <see cref="ILogger" />, at <see cref="LogLevel.Debug" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Parameter values are omitted by default, and that is a deliberate divergence from
///         Marten.</b> See <see cref="LogParameterValues" />.
///     </para>
///     <para>
///         <b>One instance serves every session</b>, because <see cref="StartSession" /> returns
///         <c>this</c> — Marten's <c>DefaultMartenLogger</c> does the same, and the reason is that a
///         per-session logger would be an allocation per session for a facility that is usually off.
///         The consequence is that <see cref="_timestamp" /> is shared: with two sessions executing
///         concurrently the reported duration of a command is the time since <em>whichever</em>
///         session last called <see cref="OnBeforeExecute" />. The SQL and the parameters are always
///         right; only the millisecond number can be wrong, and only under concurrency. A logger that
///         needs an exact duration per session returns a new instance from <c>StartSession</c>.
///     </para>
/// </remarks>
public class DefaultFisherLogger: IFisherLogger, IFisherSessionLogger
{
    private long? _timestamp;

    public DefaultFisherLogger(ILogger inner, bool logParameterValues = false)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        LogParameterValues = logParameterValues;
    }

    /// <summary>
    ///     The host logger this writes to.
    /// </summary>
    public ILogger Inner { get; }

    /// <summary>
    ///     Whether the <em>values</em> bound to a command's parameters are written to the log.
    ///     Defaults to <c>false</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Marten logs them and Fisher does not, which is a divergence taken on purpose rather
    ///         than an omission.</b> Three things stack up on this side of it.
    ///     </para>
    ///     <para>
    ///         <b>Fisher already answered this question once, the same way.</b> <c>ToSql</c> renders
    ///         parameter names and not values — the documented reason being "so the text is readable
    ///         rather than executable". Defaulting the logger the other way would leave one store with
    ///         two opposite answers to "may Fisher write bound values somewhere a human will read
    ///         them", and the quieter of the two answers would be the one nobody chose.
    ///     </para>
    ///     <para>
    ///         <b>What is bound here is the whole document.</b> A Fisher upsert binds the serialized
    ///         document body as a single parameter, and an event append binds the event body the same
    ///         way. So "log the parameter values" does not mean an id and a timestamp; it means every
    ///         field of every document and every event the application writes, verbatim, at
    ///         <c>Debug</c>. That is true of Marten too — but the third point is not.
    ///     </para>
    ///     <para>
    ///         <b>Fisher is embedded, so the blast radius is different.</b> Marten's logs are a
    ///         server-side application's logs. Fisher runs in-process next to its database file, very
    ///         often on a desktop, an edge box or a device, where the log is a file on the same disk
    ///         and is exactly the artifact that gets attached to a support ticket or shipped to a crash
    ///         reporter. Turning on <c>Debug</c> to find out why a query is slow should not be the same
    ///         gesture as exporting the database.
    ///     </para>
    ///     <para>
    ///         <b>What is logged instead is the parameter's name and the CLR type of the bound
    ///         value</b> — <c>@p0: (String)</c>, or <c>@p0: (null)</c>. That is not a placeholder: it
    ///         is the diagnostic for Fisher's sharpest binding trap, where a <see cref="Guid" /> bound
    ///         without conversion is written as a 16-byte BLOB that can never match the TEXT the schema
    ///         holds, and every read silently returns nothing. A line reading <c>(Guid)</c> where
    ///         <c>(String)</c> belongs says that immediately; the value would not.
    ///     </para>
    ///     <para>
    ///         Set it to <c>true</c> — with <c>new DefaultFisherLogger(logger, logParameterValues:
    ///         true)</c>, or with <see cref="StoreOptions.LogSqlParameterValues" /> — and Fisher
    ///         behaves as Marten does. <b>This governs the shipped logger only.</b> The interface hands
    ///         a custom logger the live <see cref="DbCommand" />, values and all; what it does with
    ///         them is its own decision, which is the honest boundary for a seam whose whole point is
    ///         that an application can log whatever it wants.
    ///     </para>
    /// </remarks>
    public bool LogParameterValues { get; }

    /// <inheritdoc />
    /// <remarks>
    ///     The one call per command that decides whether anything else here runs. <c>ILogger</c>
    ///     resolves its own level filtering, and a host can change that at runtime, so this is asked
    ///     every time rather than cached at construction.
    /// </remarks>
    public virtual bool Enabled => Inner.IsEnabled(LogLevel.Debug);

    public IFisherSessionLogger StartSession(IQuerySession session) => this;

    public void OnBeforeExecute(DbCommand command) => _timestamp = Stopwatch.GetTimestamp();

    public virtual void LogSuccess(DbCommand command)
        => Inner.LogDebug("Fisher executed in {Milliseconds} ms, SQL: {SQL}\n{Parameters}",
            ElapsedMilliseconds(), command.CommandText, DescribeParameters(command));

    public virtual void LogFailure(DbCommand command, Exception ex)
        => Inner.LogError(ex, "Fisher encountered an exception executing\n{SQL}\n{Parameters}",
            command.CommandText, DescribeParameters(command));

    public virtual void LogFailure(Exception ex, string message)
        => Inner.LogError(ex, "Fisher encountered an exception: {Message}", message);

    public virtual void RecordSavedChanges(IDocumentSession session, Services.IChangeSet commit)
    {
        var counts = CommitCounts.For(commit);

        Inner.LogDebug(
            "Fisher committed {OperationCount} operations in {Milliseconds} ms — {UpdatedCount} updates, "
            + "{InsertedCount} inserts, {DeletedCount} deletions, {EventCount} events across "
            + "{StreamCount} streams",
            counts.Operations, ElapsedMilliseconds(), counts.Updated, counts.Inserted, counts.Deleted,
            counts.Events, counts.Streams);
    }

    /// <summary>
    ///     Milliseconds since the last <see cref="OnBeforeExecute" />, or zero if there has not been
    ///     one — which is the case for <see cref="RecordSavedChanges" /> on a commit that queued
    ///     nothing.
    /// </summary>
    private double ElapsedMilliseconds()
        => _timestamp is { } started ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : 0d;

    /// <summary>
    ///     One line per parameter: always the name, then either the bound value or its CLR type
    ///     depending on <see cref="LogParameterValues" />.
    /// </summary>
    /// <remarks>
    ///     Only ever called from inside a member the call site has already gated on
    ///     <see cref="Enabled" />, so the <see cref="StringBuilder" /> is paid for by a store that
    ///     asked to log and by nothing else.
    /// </remarks>
    internal string DescribeParameters(DbCommand command) => Describe(command, LogParameterValues);

    internal static string Describe(DbCommand command, bool includeValues)
    {
        var parameters = command.Parameters;

        if (parameters.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];

            if (i > 0)
            {
                builder.AppendLine();
            }

            builder.Append("  ").Append(parameter.ParameterName).Append(": ");

            if (includeValues)
            {
                builder.Append(parameter.Value ?? "NULL");
            }
            else
            {
                // The CLR type of what was bound rather than a bare "(hidden)", because that is the
                // question a parameter line is usually being read to answer — see LogParameterValues.
                builder.Append('(')
                    .Append(parameter.Value is null or DBNull ? "null" : parameter.Value.GetType().Name)
                    .Append(')');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    ///     The counts a commit line reports, pulled off an <see cref="Services.IChangeSet" /> once.
    /// </summary>
    /// <remarks>
    ///     Every member of the change set is an <see cref="IEnumerable{T}" />, so counting each one
    ///     twice — once for the total and once for its own slot — would walk the unit of work six
    ///     times. Extracted so a custom logger can have the same numbers without repeating that.
    /// </remarks>
    internal readonly record struct CommitCounts(
        int Updated, int Inserted, int Deleted, int Events, int Streams)
    {
        /// <summary>Documents written, however they were written, plus deletions.</summary>
        public int Operations => Updated + Inserted + Deleted;

        public static CommitCounts For(Services.IChangeSet commit)
            => new(commit.Updated.Count(), commit.Inserted.Count(), commit.Deleted.Count(),
                commit.GetEvents().Count(), commit.GetStreams().Count());
    }
}

/// <summary>
///     The logger a store has when nobody attached one: it records nothing, and says so through
///     <see cref="IFisherSessionLogger.Enabled" /> so that no call site builds an argument for it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The <c>Enabled</c> override is the whole point of the class, not a detail.</b> Marten's
///         <c>NulloMartenLogger</c> derives from <c>DefaultMartenLogger</c> over a <c>NullLogger</c>,
///         so an unlogged Marten command still makes a virtual call and an <c>IsEnabled</c> check per
///         execution and discards the result inside. Fisher's answers <c>false</c> from a constant, and
///         every call site checks that before touching anything else — so an unattached store pays one
///         interface call returning a constant, on a singleton, and allocates nothing.
///         <c>logging_seam.the_no_logger_path_allocates_nothing</c> pins it.
///     </para>
///     <para>
///         Deriving from <see cref="DefaultFisherLogger" /> anyway, as Marten does, so that the
///         members still behave sanely if something calls one directly.
///     </para>
/// </remarks>
public sealed class NulloFisherLogger: DefaultFisherLogger
{
    private NulloFisherLogger(): base(NullLogger.Instance)
    {
    }

    /// <summary>The single instance every unlogged store and session shares.</summary>
    public static NulloFisherLogger Flyweight { get; } = new();

    /// <inheritdoc />
    public override bool Enabled => false;
}
