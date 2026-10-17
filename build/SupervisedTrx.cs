using System.Xml.Linq;
using Bobcat.Supervisor;
using Nuke.Common.IO;

/// <summary>
/// Writes a supervised run as the TRX the old harness produced with <c>--report-trx</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two consumers read a suite's TRX and neither should have to learn about the supervisor:
/// scripts/check_scoreboard.py (total and per-class counts, and the names of every test that did not
/// pass) and the dorny/test-reporter step. A worker runs in MTP server mode, where <c>--report-trx</c>
/// is not available to it, so the report is written here from <see cref="SupervisorResults"/>.
/// </para>
/// <para>
/// Shaped like Microsoft.Testing.Extensions.TrxReport 1.9.1's output for the same suite: `name` and
/// `testName` are the xUnit display name (<c>Namespace.Class.method(args)</c>), `className` the class,
/// a skipped test is <c>NotExecuted</c> and counts toward <c>total</c> but not <c>executed</c>.
/// A test the supervisor never heard a verdict for (<see cref="WorkerTestState.Indeterminate"/>) is
/// written <c>Inconclusive</c> — not Passed, so the scoreboard names it as red — with the supervisor's
/// explanation as its message.
/// </para>
/// </remarks>
static class SupervisedTrx
{
    static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    const string TestType = "13CDC9D9-DDB5-4fa4-A97D-D965CCFC6D4B";
    const string ResultsNotInAList = "8C84FA94-04C1-424b-9868-57A2D4851A1D";
    const string AllLoadedResults = "19431567-8539-422a-85d7-44ee4e166bda";

    public static AbsolutePath Write(SupervisorResults results, string project, string framework, AbsolutePath directory)
    {
        directory.CreateDirectory();
        var path = directory / $"{project}-{framework}.trx";

        var finished = DateTimeOffset.UtcNow;
        var started = finished - results.Duration;

        var definitions = new XElement(Ns + "TestDefinitions");
        var entries = new XElement(Ns + "TestEntries");
        var unitResults = new XElement(Ns + "Results");

        int passed = 0, failed = 0, errors = 0, timeouts = 0, notExecuted = 0, inconclusive = 0;

        foreach (var test in results.Tests)
        {
            var outcome = test.Final.Outcome;
            var testId = deterministicGuid(test.Uid).ToString();
            var executionId = Guid.NewGuid().ToString();
            var className = outcome.TestClass ?? classOf(test.DisplayName);

            var trxOutcome = outcome.State switch
            {
                WorkerTestState.Passed => "Passed",
                WorkerTestState.Skipped => "NotExecuted",
                WorkerTestState.Failed => "Failed",
                WorkerTestState.Error => "Error",
                WorkerTestState.Timeout => "Timeout",
                WorkerTestState.Cancelled => "Aborted",
                _ => "Inconclusive"
            };

            switch (outcome.State)
            {
                case WorkerTestState.Passed: passed++; break;
                case WorkerTestState.Skipped: notExecuted++; break;
                case WorkerTestState.Failed: failed++; break;
                case WorkerTestState.Error: errors++; break;
                case WorkerTestState.Timeout: timeouts++; break;
                default: inconclusive++; break;
            }

            definitions.Add(new XElement(Ns + "UnitTest",
                new XAttribute("name", test.DisplayName),
                new XAttribute("storage", project + ".dll"),
                new XAttribute("id", testId),
                new XElement(Ns + "Execution", new XAttribute("id", executionId)),
                new XElement(Ns + "TestMethod",
                    new XAttribute("codeBase", project + ".dll"),
                    new XAttribute("adapterTypeName", "executor://bobcat-supervisor"),
                    new XAttribute("className", className),
                    new XAttribute("name", test.DisplayName))));

            entries.Add(new XElement(Ns + "TestEntry",
                new XAttribute("testId", testId),
                new XAttribute("executionId", executionId),
                new XAttribute("testListId", ResultsNotInAList)));

            var result = new XElement(Ns + "UnitTestResult",
                new XAttribute("executionId", executionId),
                new XAttribute("testId", testId),
                new XAttribute("testName", test.DisplayName),
                new XAttribute("computerName", Environment.MachineName),
                new XAttribute("duration", (outcome.Duration ?? TimeSpan.Zero).ToString("c")),
                new XAttribute("startTime", started.ToString("o")),
                new XAttribute("endTime", finished.ToString("o")),
                new XAttribute("testType", TestType),
                new XAttribute("outcome", trxOutcome),
                new XAttribute("testListId", ResultsNotInAList),
                new XAttribute("relativeResultsDirectory", executionId));

            var message = outcome.ErrorMessage;
            if (test.AttemptCount > 1)
                message = $"[{test.AttemptCount} attempts, {test.Outcome}] {message}";

            if (!string.IsNullOrEmpty(message) || !string.IsNullOrEmpty(outcome.StackTrace))
            {
                result.Add(new XElement(Ns + "Output",
                    new XElement(Ns + "ErrorInfo",
                        new XElement(Ns + "Message", message ?? ""),
                        new XElement(Ns + "StackTrace", outcome.StackTrace ?? ""))));
            }

            unitResults.Add(result);
        }

        var total = results.Tests.Count;
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Ns + "TestRun",
                new XAttribute("id", Guid.NewGuid()),
                new XAttribute("name", $"{project} ({framework}) supervised {finished:yyyy-MM-dd HH:mm:ss}"),
                new XElement(Ns + "Times",
                    new XAttribute("creation", started.ToString("o")),
                    new XAttribute("queuing", started.ToString("o")),
                    new XAttribute("start", started.ToString("o")),
                    new XAttribute("finish", finished.ToString("o"))),
                new XElement(Ns + "TestSettings",
                    new XAttribute("name", "default"),
                    new XAttribute("id", Guid.NewGuid())),
                unitResults,
                definitions,
                entries,
                new XElement(Ns + "TestLists",
                    new XElement(Ns + "TestList",
                        new XAttribute("name", "Results Not in a List"),
                        new XAttribute("id", ResultsNotInAList)),
                    new XElement(Ns + "TestList",
                        new XAttribute("name", "All Loaded Results"),
                        new XAttribute("id", AllLoadedResults))),
                new XElement(Ns + "ResultSummary",
                    new XAttribute("outcome", results.ExitCode == 0 ? "Completed" : "Failed"),
                    new XElement(Ns + "Counters",
                        new XAttribute("total", total),
                        new XAttribute("executed", total - notExecuted),
                        new XAttribute("passed", passed),
                        new XAttribute("failed", failed),
                        new XAttribute("error", errors),
                        new XAttribute("timeout", timeouts),
                        new XAttribute("aborted", 0),
                        new XAttribute("inconclusive", inconclusive),
                        new XAttribute("passedButRunAborted", 0),
                        new XAttribute("notRunnable", 0),
                        new XAttribute("notExecuted", notExecuted),
                        new XAttribute("disconnected", 0),
                        new XAttribute("warning", 0),
                        new XAttribute("completed", 0),
                        new XAttribute("inProgress", 0),
                        new XAttribute("pending", 0)))));

        document.Save(path);
        return path;
    }

    /// <summary>The display name's class: arguments stripped first, then everything before the last dot.</summary>
    static string classOf(string displayName)
    {
        var open = displayName.IndexOf('(');
        var name = open > 0 ? displayName[..open] : displayName;
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>A stable TRX test id from the platform uid, so two reports of one suite agree on ids.</summary>
    static Guid deterministicGuid(string uid)
    {
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(uid));
        return new Guid(hash);
    }
}
