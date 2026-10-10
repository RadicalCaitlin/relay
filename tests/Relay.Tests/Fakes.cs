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

    public IAgentSession CreateSession(AgentDiscoveryResult discovery, string workingDirectory) => Session;
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
