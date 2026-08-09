using System.Diagnostics;

namespace Fisher.Internal;

/// <summary>
///     Fisher's own <see cref="ActivitySource" /> and the spans it emits for session work
///     (fisher#48).
/// </summary>
/// <remarks>
///     <para>
///         <b>The instinct is that tracing is for network calls and an embedded store has none. That is
///         backwards for the thing operators actually hit.</b> SQLite serialises writers per database
///         file, so the interesting question about a slow Fisher call is almost always "how long did it
///         wait for the write lock" — and without a span, a request that spent its time queued behind
///         another writer is indistinguishable from one that was simply slow. A <c>SQLITE_BUSY</c>
///         retry is the single most useful thing Fisher can report, which is why
///         <see cref="RecordRetry" /> exists and why it is wired into the resilience pipeline rather
///         than bolted on afterwards.
///     </para>
///     <para>
///         <b>Instrumented inside the session, not through a decorator.</b> Polecat wraps
///         <c>IDocumentSession</c> in a <c>TracingSessionDecorator</c>; that means re-implementing
///         every member as a pass-through, a cost that grows with every feature added to the interface,
///         and it interacts badly with the daemon queueing onto the concrete session type. Every span
///         here is guarded by <see cref="ActivitySource.HasListeners" />, which is the standard cheap
///         check and makes the untraced path free — <see cref="ActivitySource.StartActivity(string,ActivityKind)" />
///         already returns null with no listeners, so the guard is only about the tag work around it.
///     </para>
///     <para>
///         The source is named <c>Fisher</c>, matching the meter and the <c>fisher</c> metrics prefix
///         the store already reports through <c>IEventStore</c>. An application subscribes with
///         <c>AddSource("Fisher")</c>.
///     </para>
/// </remarks>
internal static class FisherTracing
{
    /// <summary>The source an application subscribes to with <c>AddSource("Fisher")</c>.</summary>
    internal static readonly ActivitySource Source =
        new("Fisher", typeof(FisherTracing).Assembly.GetName().Version?.ToString());

    internal const string SaveChanges = "fisher.save_changes";
    internal const string Query = "fisher.query";
    internal const string Load = "fisher.load";

    /// <summary>
    ///     Start a span for a unit of work, or return null when nothing is listening.
    /// </summary>
    /// <remarks>
    ///     <see cref="ActivityKind.Client" /> even though there is no wire: the semantic is "this call
    ///     is talking to a database", and a trace viewer's client/server pairing is what an operator
    ///     reads to separate their own work from the store's. An embedded store is still a dependency.
    /// </remarks>
    internal static Activity? StartOperation(string name, StoreOptions options)
    {
        var activity = Source.StartActivity(name, ActivityKind.Client);

        if (activity is null)
        {
            return null;
        }

        // Enough to tell two stores apart in one process, which is the case that makes an unattributed
        // span useless — a multi-store application (fisher#46) and a store whose logical schema is its
        // isolation boundary both need this.
        activity.SetTag("db.system", "sqlite");
        activity.SetTag("fisher.store", options.StoreName);
        activity.SetTag("fisher.schema", options.DatabaseSchemaName);

        return activity;
    }

    /// <summary>
    ///     Record a <c>SQLITE_BUSY</c> retry on whatever span is current.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The highest-value thing here, and the reason it is an event on the enclosing span
    ///         rather than a span of its own.</b> A retry is not a nested operation — it is the same
    ///         operation happening again — so an operator wants to see "this save was contended three
    ///         times" against the save, not three orphan spans.
    ///     </para>
    ///     <para>
    ///         Recorded against <see cref="Activity.Current" /> rather than a captured span, because
    ///         the pipeline is shared by every path that executes SQL: a session's commit, the daemon's
    ///         batch, the Hi-Lo advance. Whichever of those is on the stack is the one that was
    ///         contended.
    ///     </para>
    /// </remarks>
    internal static void RecordRetry(int attempt, TimeSpan delay, Exception? exception)
    {
        if (Activity.Current is not { IsAllDataRequested: true } activity)
        {
            return;
        }

        activity.AddEvent(new ActivityEvent("fisher.retry", tags: new ActivityTagsCollection
        {
            { "fisher.retry.attempt", attempt },
            { "fisher.retry.delay_ms", delay.TotalMilliseconds },
            { "exception.type", exception?.GetType().Name },
            { "exception.message", exception?.Message }
        }));
    }

    /// <summary>
    ///     Mark a span failed and attach the exception, as the OpenTelemetry conventions expect.
    /// </summary>
    internal static void RecordFailure(this Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.AddException(exception);
    }
}
