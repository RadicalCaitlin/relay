using Relay.Core.Agents;
using Relay.Core.Rooms;
using Relay.Core.Routing;

namespace Relay.Tests;

public class MessageRouterTests
{
    private readonly MessageRouter _router = new();
    private readonly ChatRoom _room = new InMemoryRoomManager().CreateRoom("general");

    private FakeSession AddAgent(string id, FakeSession? session = null)
    {
        session ??= new FakeSession($"hello from {id}");
        _room.AddParticipant(new AgentParticipant(id, id.ToUpperInvariant(), session));
        return session;
    }

    [Theory]
    [InlineData("@claude hello there", "claude", "hello there")]
    [InlineData("  @Claude: hello", "Claude", "hello")]
    [InlineData("@claude, line one\nline two", "claude", "line one\nline two")]
    [InlineData("@claude", "claude", "")]
    [InlineData("@claude:fix the build", "claude", "fix the build")]
    [InlineData("@claude,hello", "claude", "hello")]
    public void Mention_parses_a_leading_target(string text, string target, string body)
    {
        Assert.True(Mention.TryParse(text, out var actualTarget, out var actualBody));
        Assert.Equal(target, actualTarget);
        Assert.Equal(body, actualBody);
    }

    [Theory]
    [InlineData("hello @claude")]
    [InlineData("mail me at a@b.com")]
    [InlineData("@ claude hi")]
    [InlineData("@claude.md is out of date")]
    public void Mention_ignores_text_without_a_leading_target(string text)
        => Assert.False(Mention.TryParse(text, out _, out _));

    // Only the first line addresses anyone, so text pasted into a message can never invite an agent
    // (ADR 0002). Before pasted lines became one message, no human message could hold a newline.
    [Fact]
    public async Task A_mention_on_a_later_line_does_not_invite_anyone()
    {
        var claude = AddAgent("claude", new FakeSession("@codex\nover to you"));
        var codex = AddAgent("codex");

        // A first line holding nothing but a mention: the separator after it would otherwise run on
        // into the next line, and the mention there would be read as a second agent being named.
        var result = await _router.SendHumanMessageAsync(_room, "@claude\n@codex and you too");

        // Codex was never invited, so Claude's handover to it is not delivered: had the second line
        // counted as an invitation, the exchange would have run and Codex would have had a turn.
        Assert.Equal("claude", result.Message.TargetId);
        Assert.Empty(codex.Received);
        // The line the user did not mean as an address is part of the message, where it belongs.
        Assert.Contains("@codex and you too", Assert.Single(claude.Received));
    }

    [Fact]
    public async Task A_mention_alone_on_a_later_line_does_not_address_anyone()
    {
        var claude = AddAgent("claude");

        var result = await _router.SendHumanMessageAsync(_room, "\n@claude review this");

        Assert.Equal(StopReason.NotAddressed, result.Reason);
        Assert.Empty(claude.Received);
        Assert.Null(Assert.Single(_room.Messages).TargetId);
    }

    [Fact]
    public async Task The_lines_after_the_first_keep_their_own_spacing()
    {
        var claude = AddAgent("claude");

        await _router.SendHumanMessageAsync(_room, "@claude fix this\n\n    indented\n");

        Assert.Contains("fix this\n\n    indented\n", Assert.Single(claude.Received));
    }

    [Fact]
    public async Task Untargeted_message_is_recorded_and_sent_to_nobody()
    {
        var session = AddAgent("claude");

        var result = await _router.SendHumanMessageAsync(_room, "hello everyone");

        Assert.Empty(result.Exchange);
        Assert.Equal(StopReason.NotAddressed, result.Reason);
        Assert.Empty(session.Received);
        var message = Assert.Single(_room.Messages);
        Assert.Equal(SenderType.Human, message.SenderType);
        Assert.Equal(ChatRoom.HumanId, message.SenderId);
        Assert.Null(message.TargetId);
    }

    [Fact]
    public async Task Targeted_message_reaches_only_that_agent_and_its_reply_joins_the_history()
    {
        var claude = AddAgent("claude");
        var codex = AddAgent("codex");

        var result = await _router.SendHumanMessageAsync(_room, "@claude say hello");

        Assert.Equal(["say hello"], claude.Received);
        Assert.Empty(codex.Received);
        Assert.Equal("claude", result.Message.TargetId);
        var reply = Assert.Single(result.Exchange);
        Assert.Equal(StopReason.RepliedToUser, result.Reason);
        Assert.Equal([result.Message, reply], _room.Messages);
        Assert.Equal(SenderType.Agent, reply.SenderType);
        Assert.Equal("claude", reply.SenderId);
        Assert.Equal("hello from claude", reply.Content);
    }

    [Fact]
    public async Task Agent_reply_that_mentions_another_agent_is_not_routed_onward()
    {
        AddAgent("claude", new FakeSession("@codex please take over"));
        var codex = AddAgent("codex");

        var result = await _router.SendHumanMessageAsync(_room, "@claude hi");

        Assert.Empty(codex.Received);
        Assert.Equal(StopReason.RepliedToUser, result.Reason);
        Assert.Equal(2, _room.Messages.Count);
    }

    [Fact]
    public async Task Unavailable_agent_produces_a_system_notice_with_the_reason()
    {
        _room.AddParticipant(new AgentParticipant("codex", "Codex", session: null, "'codex' was not found on PATH"));

        var result = await _router.SendHumanMessageAsync(_room, "@codex hi");

        var notice = Assert.Single(result.Exchange);
        Assert.Equal(SenderType.System, notice.SenderType);
        Assert.Equal("Codex is unavailable: 'codex' was not found on PATH.", notice.Content);
        Assert.Equal(2, _room.Messages.Count);
    }

    [Fact]
    public async Task Unknown_agent_produces_a_system_notice_listing_the_room_agents()
    {
        AddAgent("claude");

        var result = await _router.SendHumanMessageAsync(_room, "@gemini hi");

        var notice = Assert.Single(result.Exchange);
        Assert.Equal(SenderType.System, notice.SenderType);
        Assert.Contains("@gemini", notice.Content);
        Assert.Contains("@claude", notice.Content);
        Assert.Equal("gemini", result.Message.TargetId);
    }

    [Fact]
    public async Task Dispatch_callback_fires_only_when_a_message_is_handed_to_an_agent()
    {
        AddAgent("claude");
        _room.AddParticipant(new AgentParticipant("codex", "Codex", session: null, "not installed"));
        var dispatched = new List<string>();

        foreach (var text in new[] { "hello", "@gemini hi", "@codex hi", "@claude", "@claude hi" })
            await _router.SendHumanMessageAsync(_room, text, new() { TurnStarting = starting => dispatched.Add(starting.Agent.Id) });

        Assert.Equal(["claude"], dispatched);
    }

    [Fact]
    public async Task Mention_without_a_message_is_not_sent()
    {
        var session = AddAgent("claude");

        var result = await _router.SendHumanMessageAsync(_room, "@claude");

        Assert.Empty(session.Received);
        var notice = Assert.Single(result.Exchange);
        Assert.Equal(SenderType.System, notice.SenderType);
    }

    [Fact]
    public async Task Agent_failure_becomes_a_system_notice()
    {
        AddAgent("claude", new FakeSession((_, _) => Task.FromException<string>(new AgentException("Not logged in"))));

        var result = await _router.SendHumanMessageAsync(_room, "@claude hi");

        var notice = Assert.Single(result.Exchange);
        Assert.Equal(SenderType.System, notice.SenderType);
        Assert.Equal("CLAUDE failed: Not logged in", notice.Content);
    }

    [Fact]
    public async Task Unexpected_provider_exception_becomes_a_system_notice_instead_of_escaping()
    {
        AddAgent("claude", new FakeSession((_, _) => Task.FromException<string>(new InvalidOperationException("provider bug"))));

        var result = await _router.SendHumanMessageAsync(_room, "@claude hi");

        var notice = Assert.Single(result.Exchange);
        Assert.Equal(SenderType.System, notice.SenderType);
        Assert.Equal("CLAUDE failed unexpectedly (InvalidOperationException): provider bug", notice.Content);
        Assert.Equal(2, _room.Messages.Count);
    }

    [Fact]
    public async Task Cancelled_request_becomes_a_system_notice()
    {
        AddAgent("claude", new FakeSession(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return "never";
        }));
        using var cts = new CancellationTokenSource();

        var pending = _router.SendHumanMessageAsync(_room, "@claude hi", cancellationToken: cts.Token);
        cts.Cancel();
        var result = await pending;

        var notice = Assert.Single(result.Exchange);
        Assert.Equal(SenderType.System, notice.SenderType);
        Assert.Contains("cancelled", notice.Content);
    }
}
