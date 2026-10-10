using Relay.Core.Agents;

namespace Relay.Core.Rooms;

/// <param name="id">The provider id, which is also the participant's <c>@mention</c> handle.</param>
public sealed class AgentParticipant(
    string id,
    string displayName,
    IAgentSession? session,
    string? unavailableReason = null)
{
    public string Id { get; } = id;

    public string DisplayName { get; } = displayName;

    /// <summary>Null when the provider is not operational on this machine.</summary>
    public IAgentSession? Session { get; } = session;

    public string? UnavailableReason { get; } = unavailableReason;

    public AgentSessionState State => Session?.State ?? AgentSessionState.Unavailable;

    private ChatRoom? _room;

    /// <summary>The one room this agent takes part in, once it has been added to it.</summary>
    public ChatRoom? Room => _room;

    // Atomic, because two rooms adding the same agent each hold only their own lock.
    internal void Join(ChatRoom room)
    {
        if (Interlocked.CompareExchange(ref _room, room, null) is { } current)
            throw new InvalidOperationException($"'{Id}' already takes part in room '{current.Name}'. An agent's session is never shared between rooms.");
    }

    public static AgentParticipant Create(DiscoveredAgent agent, AgentSessionOptions options)
    {
        var (provider, result) = agent;
        return result.IsInstalled
            ? new(provider.Id, provider.DisplayName, provider.CreateSession(result, options))
            : new(provider.Id, provider.DisplayName, session: null, result.Detail ?? "not installed");
    }
}
