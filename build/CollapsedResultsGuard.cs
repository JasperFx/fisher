using System.Collections.Concurrent;
using Bobcat.Supervisor;

/// <summary>
/// Catches several results reported under ONE test id — a theory xUnit could not enumerate at
/// discovery (JasperFx/bobcat#565).
/// </summary>
/// <remarks>
/// <para>
/// A theory whose data xUnit cannot serialize (fisher's <c>reused_command_binding</c> carried a
/// <c>DBNull.Value</c>) is discovered as one test, and only expands into rows when it runs. Every row
/// then reports under that one test's uid, each with its own display name. The supervisor keeps one
/// verdict per uid, so the LAST row to finish is the only one in the results: measured on this suite, a
/// deliberately failing first row came back as a green run with seven of eight cases missing from the
/// count. A direct run counts every row, which is why the old harness never noticed.
/// </para>
/// <para>
/// So the build listens to the live update stream, which does carry every row, and refuses the run when
/// one uid finished under more than one display name — naming the theory, and saying whether a row the
/// results do not show had failed. The fix is in the test: give the theory serializable data (strings,
/// numbers, enums), resolving anything else inside the test body.
/// </para>
/// </remarks>
sealed class CollapsedResultsGuard : ISupervisorObserver
{
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WorkerTestState>> _terminal = new();

    public void TestUpdated(WorkerLaunchContext worker, WorkerTestUpdate update)
    {
        if (update.InProgress || update.State is not { } state) return;

        _terminal.GetOrAdd(update.Uid, _ => new ConcurrentDictionary<string, WorkerTestState>())
            .AddOrUpdate(update.DisplayName, state, (_, previous) => worse(previous, state));
    }

    /// <summary>Every uid that finished under more than one display name, with each row's verdict.</summary>
    public IReadOnlyList<CollapsedTest> Collapsed()
        => _terminal
            .Where(pair => pair.Value.Count > 1)
            .Select(pair => new CollapsedTest(pair.Key, pair.Value.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => (x.Key, x.Value)).ToList()))
            .OrderBy(x => x.Rows[0].DisplayName, StringComparer.Ordinal)
            .ToList();

    // A later attempt of the same row (a retry) must not let a pass paper over the failure before it.
    static WorkerTestState worse(WorkerTestState a, WorkerTestState b)
        => a is WorkerTestState.Passed or WorkerTestState.Skipped ? b : a;
}

sealed record CollapsedTest(string Uid, IReadOnlyList<(string DisplayName, WorkerTestState State)> Rows)
{
    public bool HidesAFailure => Rows.Any(x => x.State is not (WorkerTestState.Passed or WorkerTestState.Skipped));
}
