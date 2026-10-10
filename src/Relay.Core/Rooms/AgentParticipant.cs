using Relay.Core.Agents;

namespace Relay.Core.Rooms;

public sealed class AgentParticipant(
    string id,
    string displayName,
    string providerId,
    IAgentSession? session,
    string? unavailableReason = null)
{
    public string Id { get; } = id;

    public string DisplayName { get; } = displayName;

    public string ProviderId { get; } = providerId;

    /// <summary>Null when the provider is not operational on this machine.</summary>
    public IAgentSession? Session { get; } = session;

    public string? UnavailableReason { get; } = unavailableReason;

    public AgentSessionState State => Session?.State ?? AgentSessionState.Unavailable;

    public static AgentParticipant Create(DiscoveredAgent agent, string workingDirectory)
    {
        var (provider, result) = agent;
        return result.IsInstalled
            ? new(provider.Id, provider.DisplayName, provider.Id, provider.CreateSession(result, workingDirectory))
            : new(provider.Id, provider.DisplayName, provider.Id, session: null, result.Detail ?? "not installed");
    }
}
