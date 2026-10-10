using System.Text.Json;
using Relay.Agents.Cli;
using Relay.Agents.Processes;
using Relay.Core.Agents;

namespace Relay.Agents.Codex;

/// <summary>Parses the JSONL event stream printed by <c>codex exec --json</c>.</summary>
public static class CodexOutputParser
{
    public static AgentReply Parse(ProcessResult result)
    {
        string? threadId = null, text = null, error = null, turnFailure = null;
        var sawEvent = false;

        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParse(line, out var document))
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
        if (result.ExitCode != 0 || !sawEvent)
            throw new AgentException(error?.Trim() ?? $"codex {CliFailure.Describe(result)}");
        if (string.IsNullOrWhiteSpace(text))
            throw new AgentException(error?.Trim() ?? "codex returned an empty response.");

        return new(text.Trim(), threadId);
    }

    private static bool TryParse(string line, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(line);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }
}
