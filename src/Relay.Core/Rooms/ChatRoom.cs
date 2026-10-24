using Relay.Core.Agents;

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

    private readonly Dictionary<AgentParticipant, AgentRole> _roles = [];

    // Agents who have been sent their role, so their conversation may hold it.
    private readonly HashSet<AgentParticipant> _roleStated = [];

    // Agents whose role was removed after it was stated and who have not been told yet. Their
    // conversation still holds the old instruction, so saying nothing would leave it in force.
    private readonly HashSet<AgentParticipant> _roleWithdrawn = [];

    private readonly Dictionary<AgentParticipant, int> _resetAt = [];

    // What an earlier run remembered about agents that have not joined this time, or have joined
    // without a session. Kept as it was, so that saving the room does not lose it.
    private readonly Dictionary<string, RoomAgentState> _saved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The room as an earlier run left it. Each agent's part of it is applied as the agent joins.
    /// </summary>
    public static ChatRoom Restore(RoomState state)
    {
        var room = new ChatRoom(state.Id, state.Name);
        room._messages.AddRange(state.Messages);
        foreach (var agent in state.Agents)
            room._saved[agent.Id] = agent;
        return room;
    }

    /// <summary>
    /// Raised, outside the room's lock, after anything the room remembers has changed. A handler must
    /// not throw: it runs in the middle of an exchange.
    /// </summary>
    public event Action? Changed;

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
            if (participant.Session is { } session && _saved.Remove(participant.Id, out var saved))
                Rejoin(participant, session, saved);
        }
    }

    // Only a conversation the agent can continue holds what it was shown. When it cannot be continued
    // the agent starts a new one and is shown the room again, except what /reset told it to forget.
    private void Rejoin(AgentParticipant participant, IAgentSession session, RoomAgentState saved)
    {
        var resetAt = Math.Min(saved.ResetAt, _messages.Count);
        _resetAt[participant] = resetAt;
        if (saved.Role is { } role)
            _roles[participant] = role;

        if (saved.ConversationId is { } conversation && session.Resume(conversation))
        {
            _seen[participant].UnionWith(saved.Seen);
            foreach (var (message, count) in saved.Sends)
                _sends[participant][message] = count;
            if (saved.RoleStated && saved.Role is not null)
                _roleStated.Add(participant);
            if (saved.RoleWithdrawn)
                _roleWithdrawn.Add(participant);
        }
        else
        {
            _seen[participant].UnionWith(_messages.Take(resetAt).Select(m => m.Id));
        }
    }

    /// <summary>Everything the room remembers, as it is now.</summary>
    public RoomState Snapshot()
    {
        lock (_gate)
        {
            List<RoomAgentState> agents = [];
            foreach (var participant in _participants)
            {
                if (participant.Session is not { } session)
                    continue;

                agents.Add(new(
                    participant.Id,
                    session.ConversationId,
                    [.. _seen[participant]],
                    new Dictionary<Guid, int>(_sends[participant]),
                    _resetAt.GetValueOrDefault(participant),
                    _roles.GetValueOrDefault(participant),
                    _roleStated.Contains(participant),
                    _roleWithdrawn.Contains(participant)));
            }

            return new(Id, Name, [.. _messages], [.. agents, .. _saved.Values]);
        }
    }

    public AgentParticipant? FindParticipant(string id)
    {
        lock (_gate) return Find(id);
    }

    public ChatMessage AddMessage(string senderId, SenderType senderType, string content, string? targetId = null, int addressLength = 0)
    {
        var message = new ChatMessage(Guid.NewGuid(), Id, senderId, senderType, content, DateTimeOffset.UtcNow, targetId, addressLength);
        lock (_gate) _messages.Add(message);
        Changed?.Invoke();
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
        Changed?.Invoke();
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

        Changed?.Invoke();
    }

    /// <summary>The role the user has given the agent in this room, if any.</summary>
    public AgentRole? RoleOf(AgentParticipant participant)
    {
        lock (_gate)
        {
            RequireMember(participant);
            return _roles.GetValueOrDefault(participant);
        }
    }

    /// <param name="role">Null removes the agent's role.</param>
    public void SetRole(AgentParticipant participant, AgentRole? role)
    {
        lock (_gate)
        {
            RequireMember(participant);
            if (role is not null)
            {
                _roles[participant] = role;
                _roleWithdrawn.Remove(participant);
            }
            else
            {
                // An agent that was never sent its role has nothing to be told about losing it.
                _roles.Remove(participant);
                if (_roleStated.Remove(participant))
                    _roleWithdrawn.Add(participant);
            }
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Records that the agent is being sent its role. It counts whether or not the turn completes:
    /// the agent may have read it either way.
    /// </summary>
    public void MarkRoleStated(AgentParticipant participant)
    {
        lock (_gate)
        {
            RequireMember(participant);
            if (_roles.ContainsKey(participant))
                _roleStated.Add(participant);
        }

        Changed?.Invoke();
    }

    /// <summary>Whether a role the agent was sent has been removed and it has not completed a turn since.</summary>
    public bool RoleWithdrawn(AgentParticipant participant)
    {
        lock (_gate)
        {
            RequireMember(participant);
            return _roleWithdrawn.Contains(participant);
        }
    }

    /// <summary>Records that a completed turn told the agent its role was removed.</summary>
    public void MarkRoleWithdrawalTold(AgentParticipant participant)
    {
        lock (_gate)
        {
            RequireMember(participant);
            _roleWithdrawn.Remove(participant);
        }

        Changed?.Invoke();
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
            _roleStated.Remove(participant);
            _roleWithdrawn.Remove(participant);
            _resetAt[participant] = _messages.Count;
        }

        // After the session has forgotten its conversation, so what is saved no longer names it.
        participant.Session?.Reset();
        Changed?.Invoke();
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
            _roleStated.Remove(participant);
            _roleWithdrawn.Remove(participant);
            _resetAt[participant] = 0;
        }

        participant.Session?.Reset();
        Changed?.Invoke();
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
