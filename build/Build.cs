using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

/// <summary>
/// Fisher's build. Small on purpose: compile the solution, then run the three test suites through
/// Bobcat's Supervisor (see SupervisedTests.cs, JasperFx/bobcat#565). `.github/workflows/fisher.yml`
/// runs <c>./build.sh CI --framework &lt;tfm&gt;</c>; what CI does is defined here, not in the workflow.
/// </summary>
partial class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Test);

    /// <summary>
    /// Release by default, locally as well as on CI: scripts/check_scoreboard.py reads the TRX reports
    /// out of bin/Release/&lt;tfm&gt;/TestResults, and a Debug run would leave it comparing against
    /// whatever an earlier Release run left there.
    /// </summary>
    [Parameter("Configuration to build and test - Release by default")]
    readonly string Configuration = "Release";

    /// <summary>
    /// The target frameworks to test. Both by default, run one after the other — the same as
    /// <c>dotnet test fisher.slnx</c>, which runs them serially on purpose (TestTfmsInParallel=false).
    /// CI runs one per matrix leg.
    /// </summary>
    [Parameter("Target framework(s) to test - net9.0 and net10.0 by default")]
    readonly string[] Framework = ["net9.0", "net10.0"];

    static AbsolutePath Solution => RootDirectory / "fisher.slnx";

    Target Compile => _ => _
        .Executes(() =>
        {
            // Every target framework, not just the ones under test: Fisher.Benchmarks is net10.0-only,
            // so a solution-wide -f net9.0 fails with NETSDK1005 (see fisher.yml's Build step).
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration));
        });

    Target Test => _ => _
        .DependsOn(Compile)
        .Executes(() => RunSuites(Framework));

    /// <summary>What the <c>test</c> job in fisher.yml runs. Today exactly <see cref="Test"/>.</summary>
    Target CI => _ => _
        .DependsOn(Test);
}
