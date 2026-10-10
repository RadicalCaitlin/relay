using Relay.Core.Agents;
using Relay.Core.Rooms;

namespace Relay.Core.Routing;

/// <param name="Message">The human message as recorded in the room.</param>
/// <param name="Reply">The agent response, a system notice explaining why there is none, or null when no agent was addressed.</param>
public sealed record RoutingResult(ChatMessage Message, ChatMessage? Reply);

/// <summary>
/// Routes a human message to at most one explicitly mentioned agent. Agent replies are
/// recorded but never re-routed, so agents cannot trigger each other.
/// </summary>
public sealed class MessageRouter
{
    /// <param name="onDispatch">Called just before the message is handed to an agent, so a caller can show that it is waiting.</param>
    public async Task<RoutingResult> SendHumanMessageAsync(
        ChatRoom room,
        string text,
        Action<AgentParticipant>? onDispatch = null,
        CancellationToken cancellationToken = default)
    {
        if (!Mention.TryParse(text, out var target, out var body))
            return new(room.AddMessage(ChatRoom.HumanId, SenderType.Human, text), Reply: null);

        var participant = room.FindParticipant(target);
        var message = room.AddMessage(ChatRoom.HumanId, SenderType.Human, text, participant?.Id);

        if (participant is null)
        {
            var known = string.Join(", ", room.Participants.Select(p => $"@{p.Id}"));
            return new(message, Notice(room, $"No agent named @{target} in this room. Agents here: {(known.Length > 0 ? known : "none")}."));
        }

        if (participant.Session is not { } session)
            return new(message, Notice(room, $"{participant.DisplayName} is unavailable: {participant.UnavailableReason}."));

        if (body.Length == 0)
            return new(message, Notice(room, $"Nothing to send. Try: @{participant.Id} <message>"));

        onDispatch?.Invoke(participant);
        try
        {
            var response = await session.SendAsync(body, cancellationToken);
            return new(message, room.AddMessage(participant.Id, SenderType.Agent, response, ChatRoom.HumanId));
        }
        catch (OperationCanceledException)
        {
            return new(message, Notice(room, $"Request to {participant.DisplayName} was cancelled."));
        }
        catch (AgentException ex)
        {
            return new(message, Notice(room, $"{participant.DisplayName} failed: {ex.Message}"));
        }
        catch (Exception ex)
        {
            // A provider is a plug-in boundary: whatever it throws must not take the room down with it.
            // The exception type is included because this is a bug, and the user's report is all we will get.
            return new(message, Notice(room, $"{participant.DisplayName} failed unexpectedly ({ex.GetType().Name}): {ex.Message}"));
        }
    }

    private static ChatMessage Notice(ChatRoom room, string content)
        => room.AddMessage(ChatRoom.SystemId, SenderType.System, content);
}
