using System.Text.RegularExpressions;
using Relay.Agents.Processes;
using Relay.Core.Agents;

namespace Relay.Agents.Cli;

public sealed partial class CliAgentSession(
    IProcessRunner runner,
    string executablePath,
    string workingDirectory,
    TimeSpan? turnTimeout,
    Func<string?, IReadOnlyList<string>> buildArguments,
    Func<ProcessResult, AgentReply> parseReply,
    Func<string, IEnumerable<AgentActivity>> parseActivity) : IAgentSession
{
    private readonly CancellationTokenSource _disposal = new();
    private volatile AgentSessionState _state = AgentSessionState.Ready;
    private int _busy;
    private string? _sessionId;

    public AgentSessionState State => _state;

    // Without an id to resume, every turn starts a new conversation in the CLI.
    public bool RemembersConversation => _sessionId is not null;

    public string? ConversationId => _sessionId;

    // Read back from a file, so checked again exactly as an id from the CLI's own output is.
    public bool Resume(string conversationId)
    {
        if (!SafeSessionId().IsMatch(conversationId))
            return false;

        _sessionId = conversationId;
        return true;
    }

    public async Task<string> SendAsync(string message, Action<AgentActivity>? onActivity = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_state == AgentSessionState.Closed, this);
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            throw new AgentException("The agent is still working on a previous message.") { PromptMayHaveBeenRead = false };

        var activity = onActivity is null ? null : new ActivityReporter(parseActivity, onActivity);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposal.Token);
            _state = AgentSessionState.Busy;

            var request = new ProcessRequest(executablePath, buildArguments(_sessionId))
            {
                StandardInput = message,
                WorkingDirectory = workingDirectory,
                Timeout = turnTimeout,
                OnOutputLine = activity is null ? null : activity.Read,
            };
            var reply = parseReply(await runner.RunAsync(request, linked.Token));

            // The id is echoed back onto a command line (possibly through a cmd.exe shim),
            // so anything that is not a plain token, or that could be read as a flag, is dropped.
            if (reply.SessionId is { } id && SafeSessionId().IsMatch(id))
                _sessionId = id;

            Transition(AgentSessionState.Ready);
            return reply.Text;
        }
        catch (OperationCanceledException ex)
        {
            activity?.ReportHeld(ex);
            Transition(AgentSessionState.Ready);
            throw;
        }
        catch (Exception ex)
        {
            activity?.ReportHeld(ex);
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
    private sealed class ActivityReporter(Func<string, IEnumerable<AgentActivity>> parse, Action<AgentActivity> report)
    {
        private AgentActivity? _held;

        public void Read(string line)
        {
            foreach (var activity in parse(line))
            {
                if (_held is not null)
                    report(_held);
                _held = activity.Kind == AgentActivityKind.Note ? activity : null;
                if (_held is null)
                    report(activity);
            }
        }

        // A turn that failed has no reply, so the note still held is not one: it is the last thing
        // the agent said before it stopped. Unless the failure says the same thing, it is shown.
        public void ReportHeld(Exception failure)
        {
            if (_held is { } note && note.Text != failure.Message)
                report(note);
            _held = null;
        }
    }

    private void Transition(AgentSessionState next)
    {
        if (_state != AgentSessionState.Closed)
            _state = next;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$")]
    private static partial Regex SafeSessionId();
}
