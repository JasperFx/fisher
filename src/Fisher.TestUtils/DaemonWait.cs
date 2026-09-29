namespace Fisher.TestUtils;

/// <summary>
///     The one ceiling every "wait until the daemon has caught up" in the test suites uses (fisher#329).
/// </summary>
/// <remarks>
///     <para>
///         Every such wait already polls until its condition holds, which is the convention fisher#189
///         set. So a pass costs the same whatever the ceiling is, and what the ceiling decides is only
///         how slow a host may be before the test fails. It was a literal 30 seconds in 37 places. On a
///         hosted CI runner with two vCPUs, which is also running the test process, that was
///         occasionally not enough, and the failure looked like a hang rather than a wrong answer:
///         fisher#329's 30.292 s. It is the third such sighting after #189 and #311.
///     </para>
///     <para>
///         <b>Thirty seconds locally, two minutes on CI.</b> A developer's machine that needs more than
///         30 s is reporting something worth seeing. A shared runner that needs more is reporting
///         the runner. <c>CI</c> is set to <c>true</c> by GitHub Actions and most other CI systems.
///         <c>FISHER_DAEMON_WAIT_SECONDS</c> overrides both, for a deliberately loaded local
///         reproduction.
///     </para>
///     <para>
///         <b>This is deliberately NOT used by a test that expects the wait to time out.</b> Those
///         carry their own short literal, since scaling them would only make a correct failure take
///         longer.
///     </para>
/// </remarks>
public static class DaemonWait
{
    public static TimeSpan Timeout { get; } = Resolve();

    private static TimeSpan Resolve()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("FISHER_DAEMON_WAIT_SECONDS"), out var seconds)
            && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromMinutes(2)
            : TimeSpan.FromSeconds(30);
    }
}
