namespace Fisher.Tests;

/// <summary>
///     Test classes that either capture <see cref="Console.Out" /> or write to it from a JasperFx
///     command, serialized against each other (fisher#172).
/// </summary>
/// <remarks>
///     <para>
///         <c>Console.SetOut</c> is process-wide and xUnit runs collections in parallel, so a class
///         that swaps stdout to read a command's JSON report will capture whatever any concurrently
///         running test happened to print. <c>CliJsonCapture.ParseReport</c> already brace-counts from
///         the first <c>{</c> to survive a stray *line*, but that is not enough against another test
///         that also prints a JSON object — the capture then parses somebody else's report and the
///         assertions fail somewhere that has nothing to do with the cause.
///     </para>
///     <para>
///         Same family as the lesson the tracing tests record about a process-wide
///         <c>ActivityListener</c>: the shared surface is the operating system's, so the only fix is
///         to stop two tests using it at once.
///     </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ConsoleWritingCollection
{
    public const string Name = "console";
}
