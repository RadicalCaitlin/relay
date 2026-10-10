namespace Relay.Core.Rooms;

public sealed class ChatRoom(Guid id, string name)
{
    public const string HumanId = "you";
    public const string SystemId = "relay";

    private readonly Lock _gate = new();
    private readonly List<AgentParticipant> _participants = [];
    private readonly List<ChatMessage> _messages = [];

    public Guid Id { get; } = id;

    public string Name { get; } = name;

    public IReadOnlyList<AgentParticipant> Participants
    {
        get { lock (_gate) return [.. _participants]; }
    }

    public IReadOnlyList<ChatMessage> Messages
    {
        get { lock (_gate) return [.. _messages]; }
    }

    public void AddParticipant(AgentParticipant participant)
    {
        lock (_gate)
        {
            if (Find(participant.Id) is not null)
                throw new InvalidOperationException($"Room '{Name}' already has a participant '{participant.Id}'.");
            _participants.Add(participant);
        }
    }

    public AgentParticipant? FindParticipant(string id)
    {
        lock (_gate) return Find(id);
    }

    public ChatMessage AddMessage(string senderId, SenderType senderType, string content, string? targetId = null)
    {
        var message = new ChatMessage(Guid.NewGuid(), Id, senderId, senderType, content, DateTimeOffset.UtcNow, targetId);
        lock (_gate) _messages.Add(message);
        return message;
    }

    private AgentParticipant? Find(string id)
        => _participants.Find(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
