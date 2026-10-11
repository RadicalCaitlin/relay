using Relay.Core.Agents;
using Relay.Core.Rooms;

namespace Relay.Core.Routing;

/// <summary>Limits on one exchange. See docs/adr/0002-agent-to-agent-exchanges.md.</summary>
public sealed record ExchangeLimits
{
    public ExchangeLimits(int MaxTurns = 6)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTurns, 1);
        this.MaxTurns = MaxTurns;
    }

    /// <summary>Agent turns in one exchange, counting the first and counting a turn that fails.</summary>
    public int MaxTurns { get; }
}

public enum StopReason
{
    /// <summary>The message named no agent. It was recorded and sent to nobody.</summary>
    NotAddressed,

    /// <summary>The message could not be sent to the agents it named, so no turn was taken.</summary>
    NotSent,

    /// <summary>The last reply was for the user. The ordinary end.</summary>
    RepliedToUser,

    /// <summary>The last reply addressed an agent the user had not named.</summary>
    NotInvited,

    TurnLimit,
    Failed,
    Cancelled,

    /// <summary>The last reply was a handover to its own author, or to another agent with no text.</summary>
    NothingToDeliver,

    StoppedAtPause,
}

/// <summary>What the user wants done with a handover that is being held for them.</summary>
public abstract record HandoverDecision
{
    private HandoverDecision()
    {
    }

    public sealed record Deliver : HandoverDecision;

    public sealed record Stop : HandoverDecision;

    /// <summary>Sends <paramref name="Text"/> to the handover's author as the user's message, instead of delivering it.</summary>
    public sealed record SendBack(string Text) : HandoverDecision;
}

/// <param name="TurnLimit">How many turns the exchange can have; 1 when the user named a single agent.</param>
public sealed record TurnStarting(AgentParticipant Agent, int Turn, int TurnLimit);

public sealed class ExchangeEvents
{
    /// <summary>Called just before a message is handed to an agent, so a caller can show that it is waiting.</summary>
    public Action<TurnStarting>? TurnStarting { get; init; }

    /// <summary>Called for each message recorded after the user's own, as it is recorded.</summary>
    public Action<ChatMessage>? Recorded { get; init; }

    /// <summary>
    /// Called with a handover and the agent it is for before it is delivered. Leave unset to deliver
    /// every handover without asking.
    /// </summary>
    public Func<ChatMessage, AgentParticipant, HandoverDecision>? Pause { get; init; }
}

/// <param name="Message">The human message as recorded in the room.</param>
/// <param name="Exchange">
/// Everything recorded after it, in order: agent replies, what the user sent back at a pause, and
/// Relay's notices.
/// </param>
public sealed record RoutingResult(ChatMessage Message, IReadOnlyList<ChatMessage> Exchange, StopReason Reason);

/// <summary>
/// Routes a human message to the first agent it names, together with whatever was said in the room
/// since that agent's last turn. When the message names several agents, a reply whose first line is
/// a mention of one of them is delivered to it, up to a fixed number of turns. Nothing else an agent
/// writes starts a turn. See docs/adr/0002-agent-to-agent-exchanges.md.
/// </summary>
public sealed class MessageRouter(ContextBudget? budget = null, ExchangeLimits? limits = null)
{
    private readonly ContextBudget _budget = budget ?? new();
    private readonly ExchangeLimits _limits = limits ?? new();

    public async Task<RoutingResult> SendHumanMessageAsync(
        ChatRoom room,
        string text,
        ExchangeEvents? events = null,
        CancellationToken cancellationToken = default)
    {
        if (!Mention.TryParse(text, out var first, out var body))
            return new(room.AddMessage(ChatRoom.HumanId, SenderType.Human, text), [], StopReason.NotAddressed);

        // Further mentions count only while they name an agent in the room. Anything else is where
        // the message itself begins: "@claude @Override is missing" is a question about code.
        var targets = new List<string> { first };
        while (Mention.TryParse(body, out var another, out var rest) && room.FindParticipant(another) is not null)
        {
            targets.Add(another);
            body = rest;
        }

        // An unknown name is kept as typed, so the message is never mistaken for one to the whole room.
        var message = room.AddMessage(ChatRoom.HumanId, SenderType.Human, text, room.FindParticipant(first)?.Id ?? first);
        var recorded = new List<ChatMessage>();

        ChatMessage Record(string senderId, SenderType senderType, string content, string? targetId = null)
        {
            var added = room.AddMessage(senderId, senderType, content, targetId);
            recorded.Add(added);
            events?.Recorded?.Invoke(added);
            return added;
        }

        RoutingResult End(StopReason reason, string? notice = null)
        {
            if (notice is not null)
                Record(ChatRoom.SystemId, SenderType.System, notice);
            return new(message, recorded, reason);
        }

        // Every agent named must be able to take part, so the user never gets a narrower exchange
        // than the one they asked for.
        var invited = new List<(AgentParticipant Agent, IAgentSession Session)>();
        foreach (var target in targets)
        {
            if (room.FindParticipant(target) is not { } participant)
            {
                var known = string.Join(", ", room.Participants.Select(p => $"@{p.Id}"));
                return End(StopReason.NotSent, $"No agent named @{target} in this room. Agents here: {(known.Length > 0 ? known : "none")}.");
            }

            if (participant.Session is not { } available)
                return End(StopReason.NotSent, $"{participant.DisplayName} is unavailable: {participant.UnavailableReason}.");

            if (!invited.Exists(i => i.Agent == participant))
                invited.Add((participant, available));
        }

        var mentions = string.Join(' ', invited.Select(i => $"@{i.Agent.Id}"));
        if (body.Length == 0)
            return End(StopReason.NotSent, $"Nothing to send. Try: {mentions} <message>");

        var inExchange = invited.Count > 1;
        var turnLimit = inExchange ? _limits.MaxTurns : 1;
        var (speaker, session) = invited[0];

        // What the turn answers: a message from the user, or another agent's handover.
        var current = message;
        var userText = body;

        for (var turn = 1; ; turn++)
        {
            // Everything the agent has missed. What the turn answers is part of that, but is placed
            // by SharedContext rather than left among the rest.
            var catchUp = room.UnseenBy(speaker);
            var earlier = catchUp.Messages.Where(m => m.Id != current.Id).ToList();
            var roleWithdrawn = room.RoleWithdrawn(speaker);
            var note = inExchange ? new ExchangeNote([.. invited.Select(i => i.Agent)], turnLimit - turn) : null;
            var prompt = note is not null && current.SenderType == SenderType.Agent
                ? SharedContext.ComposeHandover(room, speaker, earlier, catchUp.Withheld.Count, current, _budget, note)
                : SharedContext.Compose(room, speaker, earlier, catchUp.Withheld.Count, userText, _budget, exchange: note);

            room.MarkRoleStated(speaker);
            events?.TurnStarting?.Invoke(new(speaker, turn, turnLimit));
            string response;
            try
            {
                response = await session.SendAsync(prompt, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                room.MarkSent(speaker, catchUp.Messages);
                return End(StopReason.Cancelled, inExchange
                    ? $"The exchange was cancelled during {speaker.DisplayName}'s turn."
                    : $"Request to {speaker.DisplayName} was cancelled.");
            }
            catch (AgentException ex)
            {
                // The agent may have read the transcript before failing. It is offered once more and then
                // withheld, so a run of failures cannot pile up copies in the agent's conversation.
                if (ex.PromptMayHaveBeenRead)
                    room.MarkSent(speaker, catchUp.Messages);
                return End(StopReason.Failed, $"{speaker.DisplayName} failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                // A provider is a plug-in boundary: whatever it throws must not take the room down with it.
                // The exception type is included because this is a bug, and the user's report is all we will get.
                room.MarkSent(speaker, catchUp.Messages);
                return End(StopReason.Failed, $"{speaker.DisplayName} failed unexpectedly ({ex.GetType().Name}): {ex.Message}");
            }

            // Only inside an exchange can a reply address an agent, and only in the handover form.
            // Anywhere else a reply that begins by naming an agent is prose or code for the user.
            // Inside one, a handover to an agent in the room is recorded as addressed to it whether
            // or not it is delivered.
            string? handoverText = null;
            var addressee = inExchange && Mention.TryParseHandover(response, out var named, out handoverText) ? room.FindParticipant(named) : null;
            var reply = Record(speaker.Id, SenderType.Agent, response, addressee?.Id ?? ChatRoom.HumanId);

            // Exactly what this turn showed the agent is now seen; anything said meanwhile is not.
            // An agent that will not remember this turn is shown the room afresh every time instead.
            if (session.RemembersConversation)
                room.MarkSeen(speaker, [.. catchUp.Messages, .. catchUp.Withheld, reply]);
            if (roleWithdrawn)
                room.MarkRoleWithdrawalTold(speaker);

            if (addressee is null)
                return End(StopReason.RepliedToUser);

            if (addressee == speaker)
                return End(StopReason.NothingToDeliver, $"{speaker.DisplayName} addressed its reply to itself, so there was nothing to deliver.");

            if (string.IsNullOrEmpty(handoverText))
                return End(StopReason.NothingToDeliver, $"{speaker.DisplayName} addressed {addressee.DisplayName} without a message, so there was nothing to deliver.");

            var next = invited.Find(i => i.Agent == addressee);
            if (next.Agent is null)
            {
                // Only suggest what would work: an agent with no session cannot be invited either.
                var advice = addressee.Session is null
                    ? $"{addressee.DisplayName} is unavailable: {addressee.UnavailableReason}."
                    : $"To include it, name it at the start of your message as well: {mentions} @{addressee.Id} <message>";
                return End(StopReason.NotInvited, $"{speaker.DisplayName} addressed {addressee.DisplayName}, who was not invited to this exchange, so the message was not delivered. {advice}");
            }

            if (turn == turnLimit)
                return End(StopReason.TurnLimit, $"Stopped after {turnLimit} agent turns, the limit for one exchange. {speaker.DisplayName}'s last message was addressed to {addressee.DisplayName} and was not delivered. To carry on: @{addressee.Id} @{speaker.Id} continue");

            // Cancelling between two turns, or while the question is open, must not be followed by
            // another turn, whatever was answered.
            RoutingResult CancelledBetweenTurns()
                => End(StopReason.Cancelled, $"The exchange was cancelled before {addressee.DisplayName}'s turn. {speaker.DisplayName}'s message to {addressee.DisplayName} was not delivered.");

            if (cancellationToken.IsCancellationRequested)
                return CancelledBetweenTurns();

            var decision = events?.Pause?.Invoke(reply, addressee) ?? new HandoverDecision.Deliver();
            if (cancellationToken.IsCancellationRequested)
                return CancelledBetweenTurns();

            if (decision is HandoverDecision.Stop)
                return End(StopReason.StoppedAtPause, $"Stopped. {speaker.DisplayName}'s message to {addressee.DisplayName} was not delivered.");

            if (decision is HandoverDecision.SendBack { Text: var sentBack })
            {
                current = Record(ChatRoom.HumanId, SenderType.Human, sentBack, speaker.Id);
                userText = sentBack;
            }
            else
            {
                current = reply;
                (speaker, session) = next;
            }
        }
    }
}
