using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;

namespace Relay.Cli.Approvals;

/// <summary>What an agent's CLI wants to do, or to ask: the name of the tool and what it would be given.</summary>
internal sealed record ApprovalRequest(string ToolName, JsonElement Input);

/// <summary>
/// The end of the pipe in the Relay that runs the chatroom. Each <see cref="ApprovalBridge"/> an
/// agent CLI starts connects here once per question. The pipe has a name made up for this run and
/// can be opened only by the same user.
/// </summary>
/// <param name="decide">
/// Given a request, returns the answer for the agent's CLI. Called on a thread of its own, and may
/// wait for the user. Its token is cancelled when whoever asked has stopped waiting for the answer.
/// </param>
/// <param name="open">Opens one more end of the pipe of the given name. A seam: a test cannot otherwise make that fail.</param>
internal sealed class ApprovalServer(Func<ApprovalRequest, CancellationToken, string> decide, Func<string, NamedPipeServerStream>? open = null) : IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

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

    public static NamedPipeServerStream OpenPipe(string name)
        => new(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private NamedPipeServerStream Open() => (open ?? OpenPipe)(PipeName);

    private async Task AcceptAsync(NamedPipeServerStream first)
    {
        NamedPipeServerStream? pipe = first;
        while (!_stopping.IsCancellationRequested)
        {
            var connected = false;
            try
            {
                pipe ??= Open();
                await pipe.WaitForConnectionAsync(_stopping.Token);
                connected = true;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
            {
            }

            if (connected)
            {
                var accepted = pipe!;
                _ = Task.Run(() => ServeAsync(accepted));
            }
            else
            {
                if (pipe is not null)
                    await pipe.DisposeAsync();

                // A pipe that cannot be opened now may open in a moment. Trying again at once would spin.
                await Task.Delay(RetryDelay, _stopping.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            pipe = null;
        }

        if (pipe is not null)
            await pipe.DisposeAsync();
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, ApprovalBridge.Utf8NoBom, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, ApprovalBridge.Utf8NoBom, leaveOpen: true) { NewLine = "\n" };
                if (await reader.ReadLineAsync(_stopping.Token) is not { } line)
                    return;

                if (!TryRead(line, out var request))
                {
                    await writer.WriteLineAsync(ApprovalBridge.Refusal("Relay could not read what was asked, so this was refused."));
                    return;
                }

                using var gone = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                var watching = WatchAsync(pipe, gone);
                try
                {
                    await writer.WriteLineAsync(decide(request, gone.Token));
                    await writer.FlushAsync();
                }
                finally
                {
                    await gone.CancelAsync();
                    await watching;
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The agent's CLI went away, or Relay is closing. Nobody is left to tell.
            }
        }
    }

    // Whoever asked sends one line and then only waits. A read that ends before the answer is
    // written means they have gone, and the user must not be left answering nobody.
    private static async Task WatchAsync(NamedPipeServerStream pipe, CancellationTokenSource gone)
    {
        try
        {
            _ = await pipe.ReadAsync(new byte[1], gone.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }

        await gone.CancelAsync();
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
