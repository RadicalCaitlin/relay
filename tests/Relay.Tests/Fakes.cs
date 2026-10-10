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

    public Task<string> SendAsync(string message, CancellationToken cancellationToken = default)
    {
        Received.Add(message);
        return respond(message, cancellationToken);
    }

    public void Reset()
    {
    }

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
        return Task.FromResult(run(request));
    }
}

internal sealed class FakeLocator(string? path) : IExecutableLocator
{
    public string? Find(string command) => path;
}

/// <summary>A process that keeps running until the test completes or cancels it.</summary>
internal sealed class BlockingProcessRunner : IProcessRunner
{
    private readonly TaskCompletionSource<ProcessResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Complete(ProcessResult result) => _result.TrySetResult(result);

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        Started.TrySetResult();
        await using var registration = cancellationToken.Register(() => _result.TrySetCanceled(cancellationToken));
        return await _result.Task;
    }
}