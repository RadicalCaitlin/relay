using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Relay.Cli.Approvals;

/// <summary>What an agent's CLI wants to do, or to ask: the name of the tool and what it would be given.</summary>
internal sealed record ApprovalRequest(string ToolName, JsonElement Input);

/// <summary>
/// The end of the pipe in the Relay that runs the chatroom. Each <see cref="ApprovalBridge"/> an
/// agent CLI starts connects here once per question. The pipe has a name made up for this run and
/// can be opened only by the same user.
/// </summary>
/// <param name="decide">Given a request, returns the answer for the agent's CLI. Called on a thread of its own, and may wait for the user.</param>
internal sealed class ApprovalServer(Func<ApprovalRequest, string> decide) : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly CancellationTokenSource _stopping = new();
    private Task? _accepting;

    // Short, because on macOS and Linux the name becomes part of a socket's path in the temporary
    // folder, and such a path may be no longer than about a hundred characters.
    public string PipeName { get; } = $"relay-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}";

    /// <exception cref="IOException">The pipe could not be opened, so nobody can be asked.</exception>
    public void Start()
    {
        NamedPipeServerStream first;
        try
        {
            first = Open();
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new IOException(ex.Message, ex);
        }

        _accepting = Task.Run(() => AcceptAsync(first));
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        if (_accepting is not null)
            await _accepting;
        _stopping.Dispose();
    }

    private NamedPipeServerStream Open()
        => new(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task AcceptAsync(NamedPipeServerStream first)
    {
        NamedPipeServerStream? next = first;
        while (!_stopping.IsCancellationRequested)
        {
            var pipe = next ?? Open();
            next = null;
            try
            {
                await pipe.WaitForConnectionAsync(_stopping.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                await pipe.DisposeAsync();
                continue;
            }

            _ = Task.Run(() => ServeAsync(pipe));
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Utf8NoBom, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, Utf8NoBom, leaveOpen: true) { NewLine = "\n" };
                if (await reader.ReadLineAsync(_stopping.Token) is not { } line)
                    return;

                var answer = TryRead(line, out var request)
                    ? decide(request)
                    : ApprovalBridge.Refusal("Relay could not read what was asked, so this was refused.");
                await writer.WriteLineAsync(answer);
                await writer.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The agent's CLI went away, or Relay is closing. Nobody is left to tell.
            }
        }
    }

    private static bool TryRead(string line, out ApprovalRequest request)
    {
        request = null!;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tool_name", out var name) || name.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
                return false;

            request = new(name.GetString()!, input.Clone());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
