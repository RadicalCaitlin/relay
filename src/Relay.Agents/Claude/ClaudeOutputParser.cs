using System.Text.Json;
using Relay.Agents.Cli;
using Relay.Agents.Processes;
using Relay.Core.Agents;

namespace Relay.Agents.Claude;

/// <summary>
/// Parses what <c>claude -p --output-format stream-json --verbose</c> prints: one JSON object per
/// line as the turn goes on, ending with a <c>result</c> object that holds the reply.
/// </summary>
public static class ClaudeOutputParser
{
    // The parts of a tool's input that say what it was used on, most telling first.
    private static readonly string[] SubjectKeys = ["command", "file_path", "pattern", "url", "query", "description"];

    public static AgentReply Parse(ProcessResult result)
    {
        using (var document = FindResult(result.StandardOutput) ?? throw new AgentException($"claude {CliFailure.Describe(result)}"))
        {
            var root = document.RootElement;
            var text = root.GetStringOrNull("result");
            var isError = root.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;

            // Failures such as "Not logged in" arrive as a normal result object with is_error set.
            if (!string.IsNullOrWhiteSpace(text) && (isError || result.ExitCode != 0))
                throw new AgentException(text.Trim());
            if (isError && result.ExitCode == 0)
                throw new AgentException("claude reported an error without a message.");

            // No result field at all is not an empty answer: the output has a shape Relay does not know.
            if (result.ExitCode != 0 || text is null)
                throw new AgentException($"claude {CliFailure.Describe(result)}");
            if (string.IsNullOrWhiteSpace(text))
                throw new AgentException("claude returned an empty response.");

            return new(text.Trim(), root.GetStringOrNull("session_id"));
        }
    }

    // The result is the last line marked as one. Looking for it from the end means the rest of the
    // stream, which holds the result of every tool Claude used, is not parsed again to find it.
    private static JsonDocument? FindResult(string output)
    {
        var rest = output.AsMemory();
        while (!rest.IsEmpty)
        {
            var start = rest.Span.LastIndexOf('\n') + 1;
            var line = rest[start..].Trim();
            rest = rest[..Math.Max(start - 1, 0)];
            if (line.IsEmpty || !CliJson.TryParseObject(line, out var candidate))
                continue;
            if (candidate.RootElement.GetStringOrNull("type") == "result")
                return candidate;
            candidate.Dispose();
        }

        // Nothing is marked as the result: the output may be the result on its own, as
        // --output-format json prints it, on one line or over several.
        return CliJson.TryParseObject(output.AsMemory(), out var whole) ? whole : null;
    }

    /// <summary>What one line of the stream says Claude is doing: what it said, a tool it used, or a tool it was refused.</summary>
    public static IEnumerable<AgentActivity> ParseActivity(string line)
    {
        // What a tool returned can be a whole file, and none of it is activity. This runs on the
        // thread reading Claude's output, so such a line is passed over without being parsed.
        if (line.StartsWith("""{"type":"user",""", StringComparison.Ordinal) || !CliJson.TryParseObject(line.AsMemory(), out var document))
            return [];

        using (document)
        {
            var root = document.RootElement;
            return (root.GetStringOrNull("type"), root.GetStringOrNull("subtype")) switch
            {
                ("assistant", _) => [.. Said(root)],
                ("system", "permission_denied") => [Refused(root)],
                _ => [],
            };
        }
    }

    private static IEnumerable<AgentActivity> Said(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var block in content.EnumerateArray())
        {
            switch (block.GetStringOrNull("type"))
            {
                case "text" when block.GetStringOrNull("text") is { } text && !string.IsNullOrWhiteSpace(text):
                    yield return new(AgentActivityKind.Note, text.Trim());
                    break;
                case "tool_use":
                    yield return new(AgentActivityKind.Action, DescribeTool(block));
                    break;
            }
        }
    }

    private static string DescribeTool(JsonElement toolUse)
    {
        var name = toolUse.GetStringOrNull("name") ?? "tool";
        if (toolUse.TryGetProperty("input", out var input))
        {
            foreach (var key in SubjectKeys)
            {
                if (input.GetStringOrNull(key) is { Length: > 0 } subject)
                    return $"{name}: {subject}";
            }
        }

        return name;
    }

    private static AgentActivity Refused(JsonElement root)
    {
        var tool = root.GetStringOrNull("tool_name") ?? "A tool";
        return new(AgentActivityKind.Problem, root.GetStringOrNull("message") is { Length: > 0 } reason
            ? $"{tool} was not allowed: {reason}"
            : $"{tool} was not allowed.");
    }
}
