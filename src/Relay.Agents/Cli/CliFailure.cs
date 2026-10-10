using Relay.Agents.Processes;

namespace Relay.Agents.Cli;

internal static class CliFailure
{
    private const int MaxLength = 400;

    /// <summary>Summarises a failed run from its exit code and the tail of stderr.</summary>
    public static string Describe(ProcessResult result)
    {
        var lines = result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tail = string.Join(" ", lines.TakeLast(3));
        if (tail.Length > MaxLength)
            tail = tail[^MaxLength..];

        return tail.Length > 0
            ? $"exited with code {result.ExitCode}: {tail}"
            : $"exited with code {result.ExitCode} and no output.";
    }
}
