using System.Text.Json;
using Relay.Agents.Cli;
using Relay.Agents.Processes;
using Relay.Core.Agents;

namespace Relay.Agents.Claude;

/// <summary>Parses the single JSON object printed by <c>claude -p --output-format json</c>.</summary>
public static class ClaudeOutputParser
{
    public static AgentReply Parse(ProcessResult result)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(result.StandardOutput);
        }
        catch (JsonException)
        {
            throw new AgentException($"claude {CliFailure.Describe(result)}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new AgentException($"claude {CliFailure.Describe(result)}");

            var text = root.GetStringOrNull("result");
            var isError = root.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;

            // Failures such as "Not logged in" arrive as a normal result object with is_error set.
            if (isError || result.ExitCode != 0)
                throw new AgentException(string.IsNullOrWhiteSpace(text) ? $"claude {CliFailure.Describe(result)}" : text.Trim());
            if (string.IsNullOrWhiteSpace(text))
                throw new AgentException("claude returned an empty response.");

            return new(text.Trim(), root.GetStringOrNull("session_id"));
        }
    }
}
