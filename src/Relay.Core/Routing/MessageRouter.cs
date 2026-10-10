using Relay.Core.Agents;
using Relay.Core.Rooms;

namespace Relay.Core.Routing;

/// <param name="Message">The human message as recorded in the room.</param>
/// <param name="Reply">The agent response, a system notice explaining why there is none, or null when no agent was addressed.</param>
public sealed record RoutingResult(ChatMessage Message, ChatMessage? Reply);

/// <summary>
/// Routes a human message to at most one explicitly mentioned agent, together with whatever was said
/// in the room since that agent's last turn. Agent replies are recorded but never re-routed, so
/// agents cannot trigger each other.
/// </summary>
public sealed class MessageRouter(ContextBudget? budget = null)
{
    private readonly ContextBudget _budget = budget ?? new();

    /// <param name="onDispatch">Called just before the message is handed to an agent, so a caller can show that it is waiting.</param>
    public async Task<RoutingResult> SendHumanMessageAsync(
        ChatRoom room,
        string text,
        Action<AgentParticipant>? onDispatch = null,
        CancellationToken cancellationToken = default)
    {
        if (!Mention.TryParse(text, out var target, out var body))
            return new(room.AddMessage(ChatRoom.HumanId, SenderType.Human, text), Reply: null);

        // An unknown name is kept as typed, so the message is never mistaken for one to the whole room.
        var participant = room.FindParticipant(target);
        var message = room.AddMessage(ChatRoom.HumanId, SenderType.Human, text, participant?.Id ?? target);

        if (participant is null)
        {
            var known = string.Join(", ", room.Participants.Select(p => $"@{p.Id}"));
            return new(message, Notice(room, $"No agent named @{target} in this room. Agents here: {(known.Length > 0 ? known : "none")}."));
        }

        if (participant.Session is not { } session)
            return new(message, Notice(room, $"{participant.DisplayName} is unavailable: {participant.UnavailableReason}."));

        if (body.Length == 0)
            return new(message, Notice(room, $"Nothing to send. Try: @{participant.Id} <message>"));

        // Everything the agent has missed. The message being sent now is part of that, but goes after
        // the transcript rather than in it.
        var catchUp = room.UnseenBy(participant);
        var earlier = catchUp.Messages.Where(m => m.Id != message.Id).ToList();
        var prompt = SharedContext.Compose(room, participant, earlier, catchUp.Withheld.Count, body, _budget);

        onDispatch?.Invoke(participant);
        try
        {
            var response = await session.SendAsync(prompt, cancellationToken);
            var reply = room.AddMessage(participant.Id, SenderType.Agent, response, ChatRoom.HumanId);

            // Exactly what this turn showed the agent is now seen; anything said meanwhile is not.
            // An agent that will not remember this turn is shown the room afresh every time instead.
            if (session.RemembersConversation)
                room.MarkSeen(participant, [.. catchUp.Messages, .. catchUp.Withheld, reply]);
            return new(message, reply);
        }
        catch (OperationCanceledException)
        {
            room.MarkSent(participant, catchUp.Messages);
            return new(message, Notice(room, $"Request to {participant.DisplayName} was cancelled."));
        }
        catch (AgentException ex)
        {
            // The agent may have read the transcript before failing. It is offered once more and then
            // withheld, so a run of failures cannot pile up copies in the agent's conversation.
            if (ex.PromptMayHaveBeenRead)
                room.MarkSent(participant, catchUp.Messages);
            return new(message, Notice(room, $"{participant.DisplayName} failed: {ex.Message}"));
        }
        catch (Exception ex)
        {
            // A provider is a plug-in boundary: whatever it throws must not take the room down with it.
            // The exception type is included because this is a bug, and the user's report is all we will get.
            room.MarkSent(participant, catchUp.Messages);
            return new(message, Notice(room, $"{participant.DisplayName} failed unexpectedly ({ex.GetType().Name}): {ex.Message}"));
        }
    }

    private static ChatMessage Notice(ChatRoom room, string content)
        => room.AddMessage(ChatRoom.SystemId, SenderType.System, content);
}
