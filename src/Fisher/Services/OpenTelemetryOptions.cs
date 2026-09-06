using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Events;
using JasperFx.OpenTelemetry;

namespace Fisher.Services;

/// <summary>
///     Fisher's OpenTelemetry <see cref="Meter" /> and the counters an application can opt into
///     (fisher#208).
/// </summary>
/// <remarks>
///     <para>
///         Fisher published an <see cref="ActivitySource" /> and no meter at all, so every question
///         about a store had to be answered by sampling individual traces. This is the other half:
///         instruments an operator can chart.
///     </para>
///     <para>
///         <b>Everything here is opt-in and nothing exists until it is asked for.</b> Each
///         <c>Track…</c> method creates its instrument on first call; until then the field is null and
///         the recording site is a null check. That matters more than usual, because the write-lock
///         site is inside every commit.
///     </para>
///     <para>
///         <b>The counters are deliberately not Marten's.</b> Marten's interesting number is
///         connection usage against a pooled remote server; Fisher's is contention for the single
///         write lock on a file. See <see cref="TrackWriteLockContention" /> for the measurement that
///         settled which instrument that is, and <see cref="TrackConnections" /> for the one inherited
///         member Fisher refuses rather than silently ignores.
///     </para>
/// </remarks>
public sealed class OpenTelemetryOptions: JasperFx.OpenTelemetry.OpenTelemetryOptions
{
    /// <summary>The meter name an application subscribes to with <c>AddMeter("Fisher")</c>.</summary>
    public const string MeterName = "Fisher";

    /// <summary>Which writer was waiting on the lock: a session commit, a daemon batch, a rebuild.</summary>
    internal const string HolderTag = "fisher.write_lock.holder";

    /// <summary>A user session's <c>SaveChangesAsync</c>.</summary>
    internal const string SessionHolder = "session";

    /// <summary>An async daemon projection batch.</summary>
    internal const string DaemonHolder = "daemon";

    /// <summary>A rebuild's teardown of existing projection state.</summary>
    internal const string RebuildHolder = "rebuild";

    /// <summary>
    ///     Which store a measurement came from.
    /// </summary>
    /// <remarks>
    ///     On every instrument, for the same reason <c>FisherTracing</c> tags every span with it: two
    ///     Fisher stores in one process are usually two <em>files</em> with a write lock each, so an
    ///     untagged write-lock series would add two unrelated queues together and read as one busy
    ///     database. It is also what lets a test filter a process-wide <c>MeterListener</c> down to its
    ///     own store — the same lesson the tracing tests learned about <c>ActivityListener</c>.
    /// </remarks>
    internal const string StoreTag = "fisher.store";

    internal const string EventTypeTag = "fisher.event.type";
    internal const string DocumentTypeTag = "fisher.document.type";
    internal const string OperationTag = "fisher.document.operation";
    internal const string TenantTag = "fisher.tenant";
    internal const string ExceptionTypeTag = "exception.type";

    // Null until the matching Track… call opts in, so a store that never asks for these pays a null
    // check on the commit path and nothing else.
    private Histogram<double>? _writeLockWait;
    private Counter<long>? _writeLockRetries;

    public OpenTelemetryOptions(): base(MeterName)
    {
    }

    /// <summary>
    ///     The <see cref="StoreOptions.StoreName" /> every measurement is tagged with, stamped by
    ///     <c>DocumentStore</c>'s constructor.
    /// </summary>
    /// <remarks>
    ///     Not read from <see cref="StoreOptions" /> directly, because these options are constructed
    ///     inside its constructor — before the name is set, and before an <c>IConfigureFisher</c>
    ///     contribution could have changed it. Stamped once when the configuration is final, which is
    ///     the same moment the flat-table rename happens and for the same reason.
    /// </remarks>
    internal string StoreName { get; set; } = "Main";

    /// <summary>
    ///     Actions applied to every committed <see cref="IChangeSet" />, from
    ///     <see cref="ExportCounterOnChangeSets{T}" /> and its shorthands.
    /// </summary>
    /// <remarks>
    ///     Empty until something opts in, and <c>DocumentStore</c> registers the listener that drives
    ///     them only when it is not — so a store with no counters has no extra listener and therefore
    ///     no extra work at commit.
    /// </remarks>
    internal List<Action<IChangeSet>> Applications { get; } = [];

    /// <summary>
    ///     <b>Not supported on Fisher.</b> Setting this to anything but
    ///     <see cref="TrackLevel.None" /> throws.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Inherited from the lifted Critter Stack base, where it means "emit span events for
    ///         opening a connection and for exceptions on one" — a useful signal against a pooled
    ///         connection to a remote server, which is what both siblings have.
    ///     </para>
    ///     <para>
    ///         <b>It would mean something different enough here to be misleading.</b> A Fisher
    ///         connection is a file handle, not a lease on a scarce server resource: Weasel's
    ///         <c>SqliteDataSource</c> is a factory rather than a pool (it builds a fresh
    ///         <c>SqliteConnection</c> on every open — see fisher#59, where measuring that was the whole
    ///         finding), and the pooling underneath is Microsoft.Data.Sqlite's own, keyed
    ///         process-wide by connection string and therefore not attributable to a store at all. A
    ///         connection count charted beside Marten's would read as the same quantity and be a
    ///         different one.
    ///     </para>
    ///     <para>
    ///         <b>Refused rather than ignored</b>, following <c>SessionOptions.IsolationLevel</c>,
    ///         which is carried for parity and refuses exactly one value by name. A knob that silently
    ///         does nothing is the failure mode this codebase keeps meeting — the absence of data is
    ///         indistinguishable from having none to report. What an operator wants from this on Fisher
    ///         is <see cref="TrackWriteLockContention" />, and the message says so.
    ///     </para>
    /// </remarks>
    public new TrackLevel TrackConnections
    {
        get => TrackLevel.None;
        set
        {
            if (value == TrackLevel.None) return;

            throw new NotSupportedException(
                "Fisher does not track connections. A SQLite connection is a file handle rather than a "
                + "lease on a pooled server resource — Weasel's SqliteDataSource builds a fresh "
                + "connection per open, and the pooling beneath it is Microsoft.Data.Sqlite's, keyed "
                + "process-wide by connection string and not attributable to a store. The number that "
                + "answers 'is this store contended' here is the wait for the write lock: call "
                + "OpenTelemetry.TrackWriteLockContention().");
        }
    }

    /// <summary>
    ///     Opt into <c>fisher.write_lock.wait</c> and <c>fisher.write_lock.retries</c>:
    ///     <b>how long a writer waited for SQLite's single write lock, and how often it gave up and
    ///     retried.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the instrument, and the pair is the point.</b> SQLite permits one writer per
    ///         database file, so the only question that matters about a loaded Fisher store is how long
    ///         its writers spent queued behind each other. <c>fisher.write_lock.wait</c> is the elapsed
    ///         time inside <c>BEGIN IMMEDIATE</c> — which is where the lock is actually taken, and
    ///         therefore where the waiting actually happens — tagged with which writer was waiting.
    ///     </para>
    ///     <para>
    ///         <b>A retry counter on its own would be the wrong instrument, and that is a measurement
    ///         rather than an opinion.</b> The obvious thing to count is <c>SQLITE_BUSY</c> retries —
    ///         Fisher's Polly pipeline already emits a <c>fisher.retry</c> activity event, and
    ///         <c>Fisher.Benchmarks</c> has a listener that counts them. Under the concurrent-writers
    ///         scenario (fisher#163) that counter reads <b>zero</b> while throughput visibly collapses,
    ///         because a contended writer sits inside <c>BEGIN IMMEDIATE</c> under the connection
    ///         string's busy timeout and eventually succeeds — it never reaches the retry. So a
    ///         dashboard built on retries alone would show a flat line through the exact incident it
    ///         exists to diagnose, which is worse than no instrument at all: a flat line reads as "not
    ///         the database".
    ///     </para>
    ///     <para>
    ///         The retry counter is still created here, beside the histogram rather than instead of it,
    ///         because the two answer different questions. A rising histogram with no retries is
    ///         ordinary contention absorbed by the busy timeout; retries mean the timeout was exceeded
    ///         or the failure was <c>SQLITE_BUSY_SNAPSHOT</c>, which the timeout does not cover at all.
    ///         They are opted into together so that neither can be charted without the other.
    ///     </para>
    ///     <para>
    ///         Milliseconds rather than seconds, because the interesting range spans four orders of
    ///         magnitude — an uncontended <c>BEGIN IMMEDIATE</c> is tens of microseconds and a
    ///         contended one runs to the busy timeout.
    ///     </para>
    /// </remarks>
    public void TrackWriteLockContention()
    {
        _writeLockWait ??= Meter.CreateHistogram<double>(
            "fisher.write_lock.wait",
            "ms",
            "Time spent waiting to take SQLite's single write lock at BEGIN IMMEDIATE");

        _writeLockRetries ??= Meter.CreateCounter<long>(
            "fisher.write_lock.retries",
            "retries",
            "SQLITE_BUSY / SQLITE_LOCKED failures the resilience pipeline retried");
    }

    /// <summary>
    ///     Whether a caller should bother timing the wait. False until
    ///     <see cref="TrackWriteLockContention" /> is called, and false again if every listener
    ///     unsubscribes.
    /// </summary>
    internal bool TracksWriteLock => _writeLockWait is { Enabled: true };

    /// <summary>
    ///     A timestamp to hand back to <see cref="RecordWriteLockWait" />, or zero when nothing is
    ///     listening.
    /// </summary>
    /// <remarks>
    ///     Returning zero rather than timing anyway is the fisher#165 discipline: the caller's cost
    ///     when this is off is one field read and a branch, not a <see cref="Stopwatch" /> call per
    ///     commit for a number that would be discarded.
    /// </remarks>
    internal long StartWriteLockWait() => TracksWriteLock ? Stopwatch.GetTimestamp() : 0L;

    /// <summary>
    ///     Record what <see cref="StartWriteLockWait" /> began. A zero <paramref name="startedAt" />
    ///     means it never began.
    /// </summary>
    internal void RecordWriteLockWait(long startedAt, string holder)
    {
        if (startedAt == 0L) return;

        var histogram = _writeLockWait;

        // Re-checked rather than trusted: a listener can go away between the two calls, and Enabled is
        // how the runtime says so.
        if (histogram is null || !histogram.Enabled) return;

        histogram.Record(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            new TagList { { StoreTag, StoreName }, { HolderTag, holder } });
    }

    /// <summary>
    ///     Record one <c>SQLITE_BUSY</c> / <c>SQLITE_LOCKED</c> retry. Called from the resilience
    ///     pipeline, beside the <c>fisher.retry</c> activity event it already emits.
    /// </summary>
    internal void RecordWriteLockRetry(Exception? exception)
    {
        var counter = _writeLockRetries;

        if (counter is null || !counter.Enabled) return;

        counter.Add(1, new TagList
        {
            { StoreTag, StoreName }, { ExceptionTypeTag, exception?.GetType().Name }
        });
    }

    /// <summary>
    ///     Add a counter applied to every committed unit of work. Mirrors Marten's
    ///     <c>ExportCounterOnChangeSets</c>.
    /// </summary>
    /// <remarks>
    ///     The extension point behind <see cref="TrackEventCounters" /> and
    ///     <see cref="TrackDocumentCounters" />, and the one to reach for when what you want to chart is
    ///     specific to your own model.
    /// </remarks>
    public void ExportCounterOnChangeSets<T>(string name, string units, Action<Counter<T>, IChangeSet> record)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(record);

        var counter = Meter.CreateCounter<T>(name, units);

        Applications.Add(commit => record(counter, commit));
    }

    /// <summary>
    ///     Opt into <c>fisher.events.appended</c>: events appended by a committed unit of work, tagged
    ///     by event type and tenant.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The same shape as Marten's <c>TrackEventCounters</c>, and it earns its place here for a
    ///         reason Marten's does not: append volume is what turns write-lock contention from a
    ///         mystery into an explanation, since on one file every appending writer is queued behind
    ///         every other. Charted against <c>fisher.write_lock.wait</c> it separates "more work
    ///         arrived" from "the same work is now waiting".
    ///     </para>
    ///     <para>
    ///         <b>A user session's appends only.</b> An async projection that raises events commits
    ///         through the daemon's batch, which deliberately does not fire session listeners — see
    ///         <see cref="IDocumentSessionListener" />. Counting those here would put the daemon's own
    ///         work on the same series as the application's and make the number mean two things.
    ///     </para>
    /// </remarks>
    public void TrackEventCounters()
        => ExportCounterOnChangeSets<long>("fisher.events.appended", "events", (counter, commit) =>
        {
            foreach (var e in commit.GetEvents())
            {
                counter.Add(1, new TagList
                {
                    { StoreTag, StoreName }, { EventTypeTag, e.EventTypeName }, { TenantTag, e.TenantId }
                });
            }
        });

    /// <summary>
    ///     Opt into <c>fisher.documents.written</c>: documents a committed unit of work inserted,
    ///     updated or deleted, tagged by document type and by which of the three it was.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Marten has no equivalent, and the reason to have one here is the same as the events
    ///         counter's: on a single-writer store every document write is time somebody else is not
    ///         writing, so "how many documents per commit" is a cause of the wait rather than a
    ///         separate subject.
    ///     </para>
    ///     <para>
    ///         Three tag values rather than three instruments, because the useful chart is the mix —
    ///         a commit shape drifting from updates to inserts is the shape that grows a file.
    ///     </para>
    /// </remarks>
    public void TrackDocumentCounters()
        => ExportCounterOnChangeSets<long>("fisher.documents.written", "documents", (counter, commit) =>
        {
            foreach (var document in commit.Updated)
            {
                counter.Add(1, new TagList
                {
                    { StoreTag, StoreName },
                    { DocumentTypeTag, document.GetType().Name },
                    { OperationTag, "update" }
                });
            }

            foreach (var document in commit.Inserted)
            {
                counter.Add(1, new TagList
                {
                    { StoreTag, StoreName },
                    { DocumentTypeTag, document.GetType().Name },
                    { OperationTag, "insert" }
                });
            }

            foreach (var deletion in commit.Deleted)
            {
                // DocumentType rather than the instance's type: a deletion by id or by predicate names
                // no instance, which is exactly the case a `deletion.Document?.GetType()` would report
                // as null for.
                counter.Add(1, new TagList
                {
                    { StoreTag, StoreName },
                    { DocumentTypeTag, deletion.DocumentType.Name },
                    { OperationTag, "delete" }
                });
            }
        });
}
