using Relay.Agents.Processes;

namespace Relay.Agents.Cli;

internal static class CliFailure
{
    private const int MaxLength = 400;

    /// <summary>
    /// Summarises a run whose output could not be used: from its exit code and the tail of stderr, or,
    /// when the CLI reported success, by saying that its output was not understood.
    /// </summary>
    public static string Describe(ProcessResult result)
    {
        if (result.ExitCode == 0)
            return "finished without an error, but Relay could not read its output. It may be a version Relay does not support; see 'relay agents'.";

        var lines = result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tail = string.Join(" ", lines.TakeLast(3));
        if (tail.Length > MaxLength)
            tail = tail[^MaxLength..];

        return tail.Length > 0
            ? $"exited with code {result.ExitCode}: {tail}"
            : $"exited with code {result.ExitCode} and no output.";
    }
}
