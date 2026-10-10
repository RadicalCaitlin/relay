namespace Relay.Core.Rooms;

/// <param name="Messages">Messages to show the agent now, oldest first.</param>
/// <param name="Withheld">
/// Messages the agent has not confirmed seeing, but which were already sent to it in earlier turns
/// that did not complete. They are not sent again, so that repeated failures cannot keep growing
/// the agent's conversation.
/// </param>
public sealed record RoomCatchUp(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ChatMessage> Withheld);

public sealed class ChatRoom(Guid id, string name)
{
    public const string HumanId = "you";
    public const string SystemId = "relay";

    /// <summary>How the human is named to agents.</summary>
    public const string HumanName = "User";

    /// <summary>How a message addressed to nobody in particular is labelled to agents.</summary>
    public const string Everyone = "everyone";

    // A message is sent to an agent at most this many times without a completed turn.
    private const int MaxSends = 2;

    private readonly Lock _gate = new();
    private readonly List<AgentParticipant> _participants = [];
    private readonly List<ChatMessage> _messages = [];

    // What each agent has been shown of this room. Kept here, not on the agent, so that it can only
    // ever refer to this room's messages.
    private readonly Dictionary<AgentParticipant, HashSet<Guid>> _seen = [];
    private readonly Dictionary<AgentParticipant, Dictionary<Guid, int>> _sends = [];

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

    /// <exception cref="InvalidOperationException">
    /// The id or name is taken or reserved, or the agent already belongs to a room.
    /// </exception>
    public void AddParticipant(AgentParticipant participant)
    {
        lock (_gate)
        {
            // Agents are told who said what by these ids and names, so none may pass for the human,
            // for Relay, or for another agent.
            if (Same(participant.Id, HumanId) || Same(participant.Id, SystemId))
                throw new InvalidOperationException($"The id '{participant.Id}' is reserved.");
            if (Same(participant.DisplayName, HumanName) || Same(participant.DisplayName, Everyone))
                throw new InvalidOperationException($"The name '{participant.DisplayName}' is reserved.");
            if (Find(participant.Id) is not null)
                throw new InvalidOperationException($"Room '{Name}' already has a participant '{participant.Id}'.");
            if (_participants.Exists(p => Same(p.DisplayName, participant.DisplayName)))
                throw new InvalidOperationException($"Room '{Name}' already has a participant named '{participant.DisplayName}'.");

            // An agent's session remembers what it was told. Sharing one between rooms would carry
            // one room's conversation into another.
            participant.Join(this);
            _participants.Add(participant);
            _seen[participant] = [];
            _sends[participant] = [];
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

    /// <summary>
    /// What has been said in the room that the agent has not seen. Relay's own notices are left out:
    /// they are about Relay, not part of the conversation.
    /// </summary>
    public RoomCatchUp UnseenBy(AgentParticipant participant)
    {
        lock (_gate)
        {
            var seen = SeenBy(participant);
            var sends = _sends[participant];
            var unseen = _messages.Where(m => m.SenderType != SenderType.System && !seen.Contains(m.Id)).ToList();
            return new(
                [.. unseen.Where(m => sends.GetValueOrDefault(m.Id) < MaxSends)],
                [.. unseen.Where(m => sends.GetValueOrDefault(m.Id) >= MaxSends)]);
        }
    }

    /// <summary>Records that the agent has seen these messages, after a turn that completed.</summary>
    public void MarkSeen(AgentParticipant participant, IEnumerable<ChatMessage> messages)
    {
        lock (_gate)
            SeenBy(participant).UnionWith(messages.Select(m => m.Id));
    }

    /// <summary>
    /// Records that these messages were sent to the agent in a turn that did not complete, so the
    /// agent may or may not have read them.
    /// </summary>
    public void MarkSent(AgentParticipant participant, IEnumerable<ChatMessage> messages)
    {
        lock (_gate)
        {
            RequireMember(participant);
            var sends = _sends[participant];
            foreach (var message in messages)
                sends[message.Id] = sends.GetValueOrDefault(message.Id) + 1;
        }
    }

    /// <summary>
    /// A clean slate: the agent forgets its conversation and everything said in the room up to now.
    /// It sees only what is said from here on.
    /// </summary>
    public void ResetConversation(AgentParticipant participant)
    {
        lock (_gate)
        {
            SeenBy(participant).UnionWith(_messages.Select(m => m.Id));
            _sends[participant].Clear();
        }

        participant.Session?.Reset();
    }

    /// <summary>
    /// A fresh start that keeps the room: the agent forgets its conversation, and is shown the room's
    /// recent messages again on its next turn. This is how a broken conversation is recovered.
    /// </summary>
    public void RestartConversation(AgentParticipant participant)
    {
        lock (_gate)
        {
            SeenBy(participant).Clear();
            _sends[participant].Clear();
        }

        participant.Session?.Reset();
    }

    private HashSet<Guid> SeenBy(AgentParticipant participant)
    {
        RequireMember(participant);
        return _seen[participant];
    }

    private void RequireMember(AgentParticipant participant)
    {
        if (!_participants.Contains(participant))
            throw new InvalidOperationException($"'{participant.Id}' is not a participant in room '{Name}'.");
    }

    private AgentParticipant? Find(string id) => _participants.Find(p => Same(p.Id, id));

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
