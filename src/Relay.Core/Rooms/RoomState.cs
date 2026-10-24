namespace Relay.Core.Rooms;

/// <summary>Everything a room remembers, so that it can be saved and opened again in a later run.</summary>
public sealed record RoomState(Guid Id, string Name, IReadOnlyList<ChatMessage> Messages, IReadOnlyList<RoomAgentState> Agents);

/// <summary>What a room remembers about one agent.</summary>
/// <param name="ConversationId">The id the agent's CLI knows its conversation by, if it has one.</param>
/// <param name="Seen">Messages the agent's conversation holds, or that <c>/reset</c> hid from it.</param>
/// <param name="Sends">How often each message was sent to it in turns that did not complete.</param>
/// <param name="ResetAt">
/// How many of the room's first messages the agent was told to forget by <c>/reset</c>. They stay
/// hidden from it even when its conversation cannot be continued.
/// </param>
public sealed record RoomAgentState(
    string Id,
    string? ConversationId,
    IReadOnlyCollection<Guid> Seen,
    IReadOnlyDictionary<Guid, int> Sends,
    int ResetAt,
    AgentRole? Role,
    bool RoleStated,
    bool RoleWithdrawn);
