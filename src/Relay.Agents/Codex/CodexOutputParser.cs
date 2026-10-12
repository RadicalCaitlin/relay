using System.Text.Json;
using System.Text.RegularExpressions;
using Relay.Agents.Cli;
using Relay.Agents.Processes;
using Relay.Core.Agents;

namespace Relay.Agents.Codex;

/// <summary>Parses the JSONL event stream printed by <c>codex exec --json</c>.</summary>
public static partial class CodexOutputParser
{
    private const string SkipCheckFlag = "--skip-git-repo-check";

    // The whole clause, because another failure may mention the flag without being this refusal.
    private const string RepositoryRefusal = $"{SkipCheckFlag} was not specified";

    public static AgentReply Parse(ProcessResult result)
    {
        string? threadId = null, text = null, error = null, turnFailure = null;
        var sawEvent = false;

        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!CliJson.TryParseObject(line.AsMemory(), out var document))
                continue;

            using (document)
            {
                var root = document.RootElement;
                var type = root.GetStringOrNull("type");
                sawEvent |= type is not null;
                switch (type)
                {
                    case "thread.started":
                        threadId = root.GetStringOrNull("thread_id");
                        break;
                    // A turn may emit several agent messages; the last one is the final answer.
                    case "item.completed" when root.TryGetProperty("item", out var item) && item.GetStringOrNull("type") == "agent_message":
                        text = item.GetStringOrNull("text");
                        break;
                    case "turn.failed" when root.TryGetProperty("error", out var failure):
                        turnFailure = failure.GetStringOrNull("message") ?? "the turn failed.";
                        break;
                    case "error":
                        error = root.GetStringOrNull("message");
                        break;
                }
            }
        }

        // An "error" event can be a transient notice (such as a reconnect) in a turn that still
        // completes, so on its own it only explains a failure; it does not cause one.
        if (turnFailure is not null)
            throw new AgentException(turnFailure.Trim());
        // Codex refuses before it starts a turn, so it has emitted no event and has read no prompt.
        if (result.ExitCode != 0 && !sawEvent && result.StandardError.Contains(RepositoryRefusal, StringComparison.Ordinal))
            throw new AgentException(
                $"codex only runs inside a Git repository, and this folder is not one. Start Relay from a repository, "
                + $"or run 'git init' here, then ask again. Relay does not pass {SkipCheckFlag} for you.")
            { PromptMayHaveBeenRead = false };
        if (result.ExitCode != 0 || !sawEvent)
            throw new AgentException(error?.Trim() ?? $"codex {CliFailure.Describe(result)}");
        if (string.IsNullOrWhiteSpace(text))
            throw new AgentException(error?.Trim() ?? "codex returned an empty response.");

        return new(text.Trim(), threadId);
    }

    /// <summary>What one event says Codex is doing: what it said, a command it started, or a command that failed.</summary>
    public static IEnumerable<AgentActivity> ParseActivity(string line)
    {
        if (!CliJson.TryParseObject(line.AsMemory(), out var document))
            return [];

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("item", out var item))
                return [];

            return (root.GetStringOrNull("type"), item.GetStringOrNull("type")) switch
            {
                ("item.completed", "agent_message") when item.GetStringOrNull("text") is { } text && !string.IsNullOrWhiteSpace(text)
                    => [new(AgentActivityKind.Note, text.Trim())],
                ("item.started", "command_execution") when item.GetStringOrNull("command") is { } command
                    => [new(AgentActivityKind.Action, $"Running: {WithoutShell(command)}")],
                ("item.completed", "command_execution") when item.GetStringOrNull("status") == "failed" && item.GetStringOrNull("command") is { } command
                    => [new(AgentActivityKind.Problem, $"Command failed{ExitCode(item)}: {WithoutShell(command)}")],
                _ => [],
            };
        }
    }

    private static string ExitCode(JsonElement item)
        => item.TryGetProperty("exit_code", out var code) && code.ValueKind == JsonValueKind.Number ? $" (exit code {code})" : string.Empty;

    // Codex reports the shell it wrapped the command in, such as pwsh.exe -Command '...' or bash -lc "...".
    // What it ran is the part worth showing. Anything else is shown as it is: another program that
    // takes -c, such as python, or a command that goes on after the quoted part.
    private static string WithoutShell(string command)
        => ShellWrapper().Match(command) is { Success: true } match ? match.Groups["script"].Value.Replace("\\\"", "\"") : command;

    private const string Shell = @"(?:bash|sh|zsh|pwsh|powershell)(?:\.exe)?";

    [GeneratedRegex(
        $"""^(?:"(?:[^"]*[\\/])?{Shell}"|(?:\S*[\\/])?{Shell})\s+(?:-Command|-lc|-c)\s+(?:'(?<script>[^']*)'|"(?<script>(?:[^"\\]|\\.)*)")$""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ShellWrapper();
}
