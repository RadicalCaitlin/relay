namespace Relay.Core.Rooms;

public enum SenderType
{
    Human,
    Agent,
    System,
}

/// <param name="AddressLength">
/// How many characters at the start of <paramref name="Content"/> are mentions the router acted
/// on, all on the first line: the agents a sent message was addressed to, or an agent's handover.
/// Display only; what the router decides is never worked out again from the text.
/// </param>
public sealed record ChatMessage(
    Guid Id,
    Guid RoomId,
    string SenderId,
    SenderType SenderType,
    string Content,
    DateTimeOffset Timestamp,
    string? TargetId = null,
    int AddressLength = 0);
