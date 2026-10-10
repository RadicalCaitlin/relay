namespace Relay.Core.Agents;

public interface IAgentProvider
{
    /// <summary>Lowercase handle used for <c>@mention</c> routing, e.g. <c>claude</c>.</summary>
    string Id { get; }

    string DisplayName { get; }

    Task<AgentDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default);

    IAgentSession CreateSession(AgentDiscoveryResult discovery, string workingDirectory);
}

public sealed record AgentDiscoveryResult(
    string ProviderId,
    string DisplayName,
    bool IsInstalled,
    string? ExecutablePath = null,
    string? Version = null,
    string? Detail = null);
