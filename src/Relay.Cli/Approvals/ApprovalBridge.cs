using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Relay.Agents.Cli;
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

    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(string pipeName, Stream input, Stream output)
    {
        using var reader = new StreamReader(input, Utf8NoBom);
        await using var writer = new StreamWriter(output, Utf8NoBom) { NewLine = "\n" };
        using var writing = new SemaphoreSlim(1);
        // The calls still waiting for the user, by request id, so the CLI can withdraw one.
        var waiting = new ConcurrentDictionary<string, CancellationTokenSource>();

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
            {
                // The CLI has stopped waiting for a call. Closing the pipe is how Relay is told.
                if (TryReadCancelled(line, out var cancelled) && waiting.TryRemove(cancelled, out var given))
                    Cancel(given);
                continue;
            }

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
                    var key = id.ToJsonString();
                    var withdrawn = new CancellationTokenSource();
                    waiting[key] = withdrawn;
                    _ = Task.Run(async () =>
                    {
                        using (withdrawn)
                        {
                            // A call the CLI withdrew is not answered: it is no longer waiting for one.
                            var answer = await AskRelayAsync(pipeName, question, withdrawn.Token);
                            waiting.TryRemove(KeyValuePair.Create(key, withdrawn));
                            if (answer is not null)
                                await SendAsync(Result(id, new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = answer }) }));
                        }
                    });
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

    // "notifications/cancelled" names the request the CLI has given up on.
    private static bool TryReadCancelled(string line, out string key)
    {
        key = string.Empty;
        if (!CliJson.TryParseObject(line.AsMemory(), out var document))
            return false;

        using (document)
        {
            var root = document.RootElement;
            if (root.GetStringOrNull("method") != "notifications/cancelled"
                || !root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object
                || !parameters.TryGetProperty("requestId", out var requestId)
                || requestId.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
                return false;

            key = requestId.GetRawText();
            return true;
        }
    }

    private static void Cancel(CancellationTokenSource withdrawn)
    {
        try
        {
            withdrawn.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It was answered at the same moment.
        }
    }

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

    /// <returns>The answer, or null when the CLI withdrew the call before there was one.</returns>
    private static async Task<string?> AskRelayAsync(string pipeName, string question, CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(ConnectTimeout, cancellationToken);
            await using var writer = new StreamWriter(pipe, Utf8NoBom, leaveOpen: true) { NewLine = "\n" };
            using var reader = new StreamReader(pipe, Utf8NoBom, leaveOpen: true);
            await writer.WriteLineAsync(question.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            return await reader.ReadLineAsync(cancellationToken) ?? Refusal("Relay stopped before the user answered, so this was refused.");
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        // Whatever went wrong, the CLI is waiting for an answer, and the only one this program may give on its own is no.
        catch (Exception)
        {
            return Refusal("The user could not be asked, so this was refused.");
        }
    }
}
