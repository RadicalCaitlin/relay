using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Relay.Core.Agents;

namespace Relay.Cli.Approvals;

/// <summary>
/// What <c>relay approval-tool &lt;pipe&gt;</c> runs: the program an agent CLI starts in order to ask
/// the user something. It speaks the Model Context Protocol on its standard streams, offers one
/// tool, and passes each call to the Relay that is running the chatroom, over a pipe only this
/// user can open. It decides nothing itself: if that Relay cannot be reached, the answer is no.
/// </summary>
internal static class ApprovalBridge
{
    public const string Command = "approval-tool";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(string pipeName, Stream input, Stream output)
    {
        using var reader = new StreamReader(input, Utf8NoBom);
        await using var writer = new StreamWriter(output, Utf8NoBom) { NewLine = "\n" };
        using var writing = new SemaphoreSlim(1);

        async Task SendAsync(JsonObject message)
        {
            await writing.WaitAsync();
            try
            {
                await writer.WriteLineAsync(message.ToJsonString());
                await writer.FlushAsync();
            }
            finally
            {
                writing.Release();
            }
        }

        // The agent's CLI ends this program by closing its input.
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!TryReadRequest(line, out var id, out var method, out var parameters))
                continue;

            switch (method)
            {
                case "initialize":
                    await SendAsync(Result(id, new JsonObject
                    {
                        ["protocolVersion"] = parameters.GetStringOrNull("protocolVersion") ?? "2025-06-18",
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "relay", ["version"] = "1" },
                    }));
                    break;
                case "ping":
                    await SendAsync(Result(id, new JsonObject()));
                    break;
                case "tools/list":
                    await SendAsync(Result(id, new JsonObject { ["tools"] = new JsonArray(Tool()) }));
                    break;
                case "tools/call" when parameters.GetStringOrNull("name") == ApprovalTool.ToolName
                    && parameters.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object:
                    // Not awaited: the user may take minutes, and the CLI must still get an answer to a ping.
                    var question = arguments.GetRawText();
                    _ = Task.Run(async () => await SendAsync(Result(id, new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = await AskRelayAsync(pipeName, question) }),
                    })));
                    break;
                default:
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id.DeepClone(),
                        ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Not supported." },
                    });
                    break;
            }
        }

        return 0;
    }

    /// <summary>What an agent CLI is told when the user said no, or could not be asked.</summary>
    public static string Refusal(string message)
        => new JsonObject { ["behavior"] = "deny", ["message"] = message }.ToJsonString();

    // A request has an id. A notification has none and is never answered.
    private static bool TryReadRequest(string line, out JsonNode id, out string? method, out JsonElement parameters)
    {
        id = null!;
        method = null;
        parameters = default;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var requestId)
                || requestId.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
                return false;

            id = JsonNode.Parse(requestId.GetRawText())!;
            method = root.GetStringOrNull("method");
            parameters = root.TryGetProperty("params", out var given) && given.ValueKind == JsonValueKind.Object ? given.Clone() : default;
            return method is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? GetStringOrNull(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonObject Result(JsonNode id, JsonObject result)
        => new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };

    private static JsonObject Tool() => new()
    {
        ["name"] = ApprovalTool.ToolName,
        ["description"] = "Asks the person using Relay whether a step may go ahead. Called by the agent's own CLI, not by the agent.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["tool_name"] = new JsonObject { ["type"] = "string" },
                ["input"] = new JsonObject { ["type"] = "object" },
            },
            ["required"] = new JsonArray("tool_name", "input"),
        },
    };

    private static async Task<string> AskRelayAsync(string pipeName, string question)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(ConnectTimeout, CancellationToken.None);
            await using var writer = new StreamWriter(pipe, Utf8NoBom, leaveOpen: true) { NewLine = "\n" };
            using var reader = new StreamReader(pipe, Utf8NoBom, leaveOpen: true);
            await writer.WriteLineAsync(question);
            await writer.FlushAsync();
            return await reader.ReadLineAsync() ?? Refusal("Relay stopped before the user answered, so this was refused.");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or ArgumentException)
        {
            return Refusal("The user could not be asked, so this was refused.");
        }
    }
}
