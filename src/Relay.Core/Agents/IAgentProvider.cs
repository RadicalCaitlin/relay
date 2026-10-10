namespace Relay.Core.Agents;

public interface IAgentProvider
{
    /// <summary>Lowercase handle used for <c>@mention</c> routing, e.g. <c>claude</c>.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <param name="includeVersion">Resolving the version may launch the agent, so it is opt-in.</param>
    Task<AgentDiscoveryResult> DiscoverAsync(bool includeVersion = false, CancellationToken cancellationToken = default);

    IAgentSession CreateSession(AgentDiscoveryResult discovery, AgentSessionOptions options);
}

/// <param name="WorkingDirectory">The directory the agent runs in.</param>
/// <param name="TrustWorkspace">
/// Whether the user has said they trust <paramref name="WorkingDirectory"/>. When false, a provider must not
/// let content in that directory configure the agent or run commands on its behalf.
/// </param>
public sealed record AgentSessionOptions(string WorkingDirectory, bool TrustWorkspace = false);

public sealed record AgentDiscoveryResult(
    string ProviderId,
    string DisplayName,
    bool IsInstalled,
    string? ExecutablePath = null,
    string? Version = null,
    string? Detail = null);
