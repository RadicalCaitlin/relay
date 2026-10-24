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

    /// <summary>
    /// The id the agent's CLI knows the conversation by, which <see cref="Resume"/> takes in a later
    /// run of Relay. Null when there is no conversation to continue.
    /// </summary>
    string? ConversationId => null;

    /// <summary>Continues a conversation from an earlier run of Relay, by the id <see cref="ConversationId"/> gave then.</summary>
    /// <returns>False, leaving the session unchanged, when the id cannot be used, so the next message starts a new conversation.</returns>
    bool Resume(string conversationId) => false;

    /// <param name="onActivity">
    /// Called with what the agent does while it works, as it happens, and never with the reply itself.
    /// It may be called from another thread, and not at all by an agent that reports nothing.
    /// </param>
    /// <exception cref="AgentException">The agent could not produce a response.</exception>
    Task<string> SendAsync(string message, Action<AgentActivity>? onActivity = null, CancellationToken cancellationToken = default);

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

    /// <summary>The agent was stopped because it ran past its time limit.</summary>
    public bool TimedOut { get; init; }
}
