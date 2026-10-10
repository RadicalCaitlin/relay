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

    /// <exception cref="AgentException">The agent could not produce a response.</exception>
    Task<string> SendAsync(string message, CancellationToken cancellationToken = default);
}

/// <summary>A failure whose message is safe and useful to show to the user.</summary>
public sealed class AgentException(string message, Exception? innerException = null)
    : Exception(message, innerException);
