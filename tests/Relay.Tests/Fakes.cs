using Relay.Agents.Processes;
using Relay.Core.Agents;

namespace Relay.Tests;

internal sealed class FakeSession(Func<string, CancellationToken, Task<string>> respond) : IAgentSession
{
    public FakeSession(string response) : this((_, _) => Task.FromResult(response))
    {
    }

    public List<string> Received { get; } = [];

    public AgentSessionState State { get; private set; } = AgentSessionState.Ready;

    public bool RemembersConversation { get; set; } = true;

    public string? ConversationId { get; set; }

    /// <summary>Whether <see cref="Resume"/> accepts a conversation from an earlier run.</summary>
    public bool CanResume { get; set; } = true;

    /// <summary>What the agent reports doing during each turn, before it replies.</summary>
    public List<AgentActivity> Activity { get; } = [];

    public Task<string> SendAsync(string message, Action<AgentActivity>? onActivity = null, CancellationToken cancellationToken = default)
    {
        Received.Add(message);
        foreach (var activity in Activity)
            onActivity?.Invoke(activity);
        return respond(message, cancellationToken);
    }

    public bool Resume(string conversationId)
    {
        if (!CanResume)
            return false;

        ConversationId = conversationId;
        return true;
    }

    public void Reset() => ConversationId = null;

    public ValueTask DisposeAsync()
    {
        State = AgentSessionState.Closed;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeProvider(string id, bool installed = true, Exception? discoveryFailure = null) : IAgentProvider
{
    public FakeSession Session { get; } = new($"hello from {id}");

    public string Id => id;

    public string DisplayName => id.ToUpperInvariant();

    public Task<AgentDiscoveryResult> DiscoverAsync(bool includeVersion = false, CancellationToken cancellationToken = default)
        => discoveryFailure is not null
            ? Task.FromException<AgentDiscoveryResult>(discoveryFailure)
            : Task.FromResult(new AgentDiscoveryResult(Id, DisplayName, installed, installed ? $"/bin/{id}" : null, Detail: installed ? null : "not installed"));

    public IAgentSession CreateSession(AgentDiscoveryResult discovery, AgentSessionOptions options) => Session;
}

internal sealed class FakeProcessRunner(Func<ProcessRequest, ProcessResult> run) : IProcessRunner
{
    public List<ProcessRequest> Requests { get; } = [];

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        var result = run(request);
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            request.OnOutputLine?.Invoke(line);
        return Task.FromResult(result);
    }
}

internal sealed class FakeLocator(string? path) : IExecutableLocator
{
    public string? Find(string command) => path;
}

/// <summary>A process that keeps running until the test completes or cancels it. Reusable, one run at a time.</summary>
internal sealed class BlockingProcessRunner : IProcessRunner
{
    private TaskCompletionSource<ProcessResult> _result = New<ProcessResult>();
    private TaskCompletionSource<bool> _started = New<bool>();

    public List<ProcessRequest> Requests { get; } = [];

    /// <summary>Completes when the next run has begun.</summary>
    public Task Started => _started.Task;

    public void Complete(ProcessResult result)
    {
        _started = New<bool>();
        _result.TrySetResult(result);
    }

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        var result = _result = New<ProcessResult>();
        _started.TrySetResult(true);
        await using var registration = cancellationToken.Register(() =>
        {
            _started = New<bool>();
            result.TrySetCanceled(cancellationToken);
        });
        return await result.Task;
    }

    private static TaskCompletionSource<T> New<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
