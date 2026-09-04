using System.Text.Json;

namespace Fisher.Tests.Events;

/// <summary>
///     Shared stdout-capture plumbing for the CLI end-to-end tests (<c>event_query_command</c>,
///     <c>stream_query_command</c>): run one command with <see cref="Console.Out" /> swapped, and
///     parse the JSON report out of whatever was captured.
/// </summary>
internal static class CliJsonCapture
{
    /// <summary>
    ///     Execute the command with stdout captured, and hand back the parsed JSON report alongside
    ///     the command's success/failure return.
    /// </summary>
    internal static async Task<(bool Success, JsonDocument Report)> RunAsync(Func<Task<bool>> execute)
    {
        var original = Console.Out;
        var captured = new StringWriter();

        bool success;
        try
        {
            Console.SetOut(captured);
            success = await execute();
        }
        finally
        {
            Console.SetOut(original);
        }

        return (success, ParseReport(captured.ToString()));
    }

    /// <summary>
    ///     Extract the report from the captured text by brace-counting from the first opening brace,
    ///     so a stray console line from a concurrently running test cannot break the parse the way a
    ///     bare <c>JsonDocument.Parse(captured)</c> would.
    /// </summary>
    internal static JsonDocument ParseReport(string captured)
    {
        var start = captured.IndexOf('{');
        start.ShouldBeGreaterThanOrEqualTo(0, $"no JSON object in captured output: '{captured}'");

        var depth = 0;
        var inString = false;
        for (var i = start; i < captured.Length; i++)
        {
            var c = captured[i];

            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}' when --depth == 0:
                    return JsonDocument.Parse(captured[start..(i + 1)]);
            }
        }

        throw new ShouldAssertException($"unbalanced JSON object in captured output: '{captured}'");
    }
}
