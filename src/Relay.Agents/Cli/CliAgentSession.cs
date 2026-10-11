using System.Text.RegularExpressions;
using Relay.Agents.Processes;
using Relay.Core.Agents;

namespace Relay.Agents.Cli;

public sealed partial class CliAgentSession(
    IProcessRunner runner,
    string executablePath,
    string workingDirectory,
    Func<string?, IReadOnlyList<string>> buildArguments,
    Func<ProcessResult, AgentReply> parseReply,
    Func<string, IEnumerable<AgentActivity>>? parseActivity = null) : IAgentSession
{
    private readonly CancellationTokenSource _disposal = new();
    private volatile AgentSessionState _state = AgentSessionState.Ready;
    private int _busy;
    private string? _sessionId;

    public AgentSessionState State => _state;

    // Without an id to resume, every turn starts a new conversation in the CLI.
    public bool RemembersConversation => _sessionId is not null;

    public async Task<string> SendAsync(string message, Action<AgentActivity>? onActivity = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_state == AgentSessionState.Closed, this);
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            throw new AgentException("The agent is still working on a previous message.") { PromptMayHaveBeenRead = false };

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposal.Token);
            _state = AgentSessionState.Busy;

            var request = new ProcessRequest(executablePath, buildArguments(_sessionId))
            {
                StandardInput = message,
                WorkingDirectory = workingDirectory,
                OnOutputLine = onActivity is null || parseActivity is null ? null : ActivityReporter(parseActivity, onActivity),
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

    // The last thing an agent says in a turn is its reply, which the caller is given as the result.
    // So a note is held back until something follows it, and the one still held at the end is dropped.
    private static Action<string> ActivityReporter(Func<string, IEnumerable<AgentActivity>> parse, Action<AgentActivity> report)
    {
        AgentActivity? held = null;
        return line =>
        {
            foreach (var activity in parse(line))
            {
                if (held is not null)
                    report(held);
                held = activity.Kind == AgentActivityKind.Note ? activity : null;
                if (held is null)
                    report(activity);
            }
        };
    }

    private void Transition(AgentSessionState next)
    {
        if (_state != AgentSessionState.Closed)
            _state = next;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$")]
    private static partial Regex SafeSessionId();
}
