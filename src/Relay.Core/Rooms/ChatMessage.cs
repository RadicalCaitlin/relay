namespace Relay.Core.Rooms;

public enum SenderType
{
    Human,
    Agent,
    System,
}

public sealed record ChatMessage(
    Guid Id,
    Guid RoomId,
    string SenderId,
    SenderType SenderType,
    string Content,
    DateTimeOffset Timestamp,
    string? TargetId = null);
