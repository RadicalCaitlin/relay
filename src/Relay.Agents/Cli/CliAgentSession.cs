using System.Text.RegularExpressions;
using Relay.Agents.Processes;
using Relay.Core.Agents;

namespace Relay.Agents.Cli;

public sealed partial class CliAgentSession(
    IProcessRunner runner,
    string executablePath,
    string workingDirectory,
    Func<string?, IReadOnlyList<string>> buildArguments,
    Func<ProcessResult, AgentReply> parseReply) : IAgentSession
{
    private readonly CancellationTokenSource _disposal = new();
    private volatile AgentSessionState _state = AgentSessionState.Ready;
    private int _busy;
    private string? _sessionId;

    public AgentSessionState State => _state;

    public async Task<string> SendAsync(string message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_state == AgentSessionState.Closed, this);
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            throw new AgentException("The agent is still working on a previous message.");

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposal.Token);
            _state = AgentSessionState.Busy;

            var request = new ProcessRequest(executablePath, buildArguments(_sessionId))
            {
                StandardInput = message,
                WorkingDirectory = workingDirectory,
            };
            var reply = parseReply(await runner.RunAsync(request, linked.Token));

            // The id is echoed back onto a command line (possibly through a cmd.exe shim),
            // so anything that is not a plain token, or that could be read as a flag, is dropped.
            if (reply.SessionId is { } id && SafeSessionId().IsMatch(id))
                _sessionId = id;

            Transition(AgentSessionState.Ready);
            return reply.Text;
        }
        catch (OperationCanceledException)
        {
            Transition(AgentSessionState.Ready);
            throw;
        }
        catch
        {
            Transition(AgentSessionState.Faulted);
            throw;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    public void Reset()
    {
        _sessionId = null;
        if (_state == AgentSessionState.Faulted)
            _state = AgentSessionState.Ready;
    }

    public ValueTask DisposeAsync()
    {
        if (_state != AgentSessionState.Closed)
        {
            _state = AgentSessionState.Closed;
            _disposal.Cancel();
            _disposal.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void Transition(AgentSessionState next)
    {
        if (_state != AgentSessionState.Closed)
            _state = next;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$")]
    private static partial Regex SafeSessionId();
}
