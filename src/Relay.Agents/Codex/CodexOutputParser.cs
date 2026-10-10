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
        string? threadId = null, text = null, error = null;

        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParse(line, out var document))
                continue;

            using (document)
            {
                var root = document.RootElement;
                switch (GetString(root, "type"))
                {
                    case "thread.started":
                        threadId = GetString(root, "thread_id");
                        break;
                    // A turn may emit several agent messages; the last one is the final answer.
                    case "item.completed" when root.TryGetProperty("item", out var item) && GetString(item, "type") == "agent_message":
                        text = GetString(item, "text");
                        break;
                    case "turn.failed" when root.TryGetProperty("error", out var failure):
                        error = GetString(failure, "message");
                        break;
                    case "error":
                        error = GetString(root, "message");
                        break;
                }
            }
        }

        if (error is not null)
            throw new AgentException(error.Trim());
        if (result.ExitCode != 0)
            throw new AgentException($"codex {CliFailure.Describe(result)}");
        if (string.IsNullOrWhiteSpace(text))
            throw new AgentException("codex returned an empty response.");

        return new(text.Trim(), threadId);
    }

    private static bool TryParse(string line, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
