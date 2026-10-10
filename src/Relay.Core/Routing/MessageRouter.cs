using Relay.Core.Agents;
using Relay.Core.Rooms;

namespace Relay.Core.Routing;

/// <param name="Message">The human message as recorded in the room.</param>
/// <param name="Reply">The agent response, a system notice explaining why there is none, or null when no agent was addressed.</param>
public sealed record RoutingResult(ChatMessage Message, ChatMessage? Reply);

public interface IMessageRouter
{
    Task<RoutingResult> SendHumanMessageAsync(ChatRoom room, string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// Routes a human message to at most one explicitly mentioned agent. Agent replies are
/// recorded but never re-routed, so agents cannot trigger each other.
/// </summary>
public sealed class MessageRouter : IMessageRouter
{
    public async Task<RoutingResult> SendHumanMessageAsync(ChatRoom room, string text, CancellationToken cancellationToken = default)
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
    }

    private static ChatMessage Notice(ChatRoom room, string content)
        => room.AddMessage(ChatRoom.SystemId, SenderType.System, content);
}
