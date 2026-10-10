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

    [Fact]
    public async Task Untargeted_message_is_recorded_and_sent_to_nobody()
    {
        var session = AddAgent("claude");

        var result = await _router.SendHumanMessageAsync(_room, "hello everyone");

        Assert.Null(result.Reply);
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
        var reply = Assert.IsType<ChatMessage>(result.Reply);
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

        await _router.SendHumanMessageAsync(_room, "@claude hi");

        Assert.Empty(codex.Received);
        Assert.Equal(2, _room.Messages.Count);
    }

    [Fact]
    public async Task Unavailable_agent_produces_a_system_notice_with_the_reason()
    {
        _room.AddParticipant(new AgentParticipant("codex", "Codex", session: null, "'codex' was not found on PATH"));

        var result = await _router.SendHumanMessageAsync(_room, "@codex hi");

        Assert.Equal(SenderType.System, result.Reply!.SenderType);
        Assert.Equal("Codex is unavailable: 'codex' was not found on PATH.", result.Reply.Content);
        Assert.Equal(2, _room.Messages.Count);
    }

    [Fact]
    public async Task Unknown_agent_produces_a_system_notice_listing_the_room_agents()
    {
        AddAgent("claude");

        var result = await _router.SendHumanMessageAsync(_room, "@gemini hi");

        Assert.Equal(SenderType.System, result.Reply!.SenderType);
        Assert.Contains("@gemini", result.Reply.Content);
        Assert.Contains("@claude", result.Reply.Content);
        Assert.Null(result.Message.TargetId);
    }

    [Fact]
    public async Task Dispatch_callback_fires_only_when_a_message_is_handed_to_an_agent()
    {
        AddAgent("claude");
        _room.AddParticipant(new AgentParticipant("codex", "Codex", session: null, "not installed"));
        var dispatched = new List<string>();

        foreach (var text in new[] { "hello", "@gemini hi", "@codex hi", "@claude", "@claude hi" })
            await _router.SendHumanMessageAsync(_room, text, agent => dispatched.Add(agent.Id));

        Assert.Equal(["claude"], dispatched);
    }

    [Fact]
    public async Task Mention_without_a_message_is_not_sent()
    {
        var session = AddAgent("claude");

        var result = await _router.SendHumanMessageAsync(_room, "@claude");

        Assert.Empty(session.Received);
        Assert.Equal(SenderType.System, result.Reply!.SenderType);
    }

    [Fact]
    public async Task Agent_failure_becomes_a_system_notice()
    {
        AddAgent("claude", new FakeSession((_, _) => Task.FromException<string>(new AgentException("Not logged in"))));

        var result = await _router.SendHumanMessageAsync(_room, "@claude hi");

        Assert.Equal(SenderType.System, result.Reply!.SenderType);
        Assert.Equal("CLAUDE failed: Not logged in", result.Reply.Content);
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

        Assert.Equal(SenderType.System, result.Reply!.SenderType);
        Assert.Contains("cancelled", result.Reply.Content);
    }
}
