using System.Runtime.CompilerServices;
using Fisher.Diagnostics;

namespace Fisher.Tests;

/// <summary>
///     Writes <see cref="DaemonTrace" />'s ring buffer to a file once, as the test process exits.
/// </summary>
/// <remarks>
///     For fisher#13. The trace itself does no I/O — probes that wrote to a file during the run made
///     the flake disappear — so the single dump has to happen after everything has finished, which is
///     what <c>ProcessExit</c> gives. The file is written on every traced run; the loop hunting the
///     flake keeps it only for the runs that failed.
/// </remarks>
internal static class DaemonTraceDump
{
    [ModuleInitializer]
    internal static void Install()
    {
        if (!DaemonTrace.Enabled)
        {
            return;
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            var path = Environment.GetEnvironmentVariable("FISHER_DAEMON_TRACE");

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                File.WriteAllText(path, DaemonTrace.Render());
            }
            catch (Exception)
            {
                // A diagnostic that fails must not fail the run it is diagnosing.
            }
        };
    }
}
