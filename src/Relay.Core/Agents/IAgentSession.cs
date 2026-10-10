namespace Relay.Core.Agents;

public enum AgentSessionState
{
    Unavailable,
    Ready,
    Busy,
    Faulted,
    Closed,
}

public interface IAgentSession : IAsyncDisposable
{
    AgentSessionState State { get; }

    /// <summary>
    /// Whether the next message will continue the conversation so far. When false the agent starts
    /// from nothing each turn, so Relay shows it the room again instead of only what is new.
    /// </summary>
    bool RemembersConversation { get; }

    /// <exception cref="AgentException">The agent could not produce a response.</exception>
    Task<string> SendAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>Forgets the agent's conversation so the next message starts a new one.</summary>
    void Reset();
}

/// <summary>A failure whose message is safe and useful to show to the user.</summary>
public sealed class AgentException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>
    /// False when the failure happened before the agent could have received anything, such as the
    /// CLI not starting. Relay then knows the agent has not seen what it was about to be sent.
    /// </summary>
    public bool PromptMayHaveBeenRead { get; init; } = true;
}
