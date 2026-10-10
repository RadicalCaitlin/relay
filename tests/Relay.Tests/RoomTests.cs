using Relay.Core.Agents;
using Relay.Core.Rooms;

namespace Relay.Tests;

public class RoomTests
{
    private readonly InMemoryRoomManager _rooms = new();

    [Fact]
    public void CreateRoom_registers_a_room_that_can_be_looked_up()
    {
        var room = _rooms.CreateRoom(" general ");

        Assert.Equal("general", room.Name);
        Assert.Same(room, _rooms.GetRoom(room.Id));
        Assert.Same(room, Assert.Single(_rooms.Rooms));
        Assert.Null(_rooms.GetRoom(Guid.NewGuid()));
    }

    [Fact]
    public void CreateRoom_rejects_a_blank_name()
        => Assert.Throws<ArgumentException>(() => _rooms.CreateRoom(" "));

    [Fact]
    public void AddMessage_appends_to_history_in_order_and_records_the_author()
    {
        var room = _rooms.CreateRoom("general");

        var first = room.AddMessage(ChatRoom.HumanId, SenderType.Human, "hi", targetId: "claude");
        var second = room.AddMessage("claude", SenderType.Agent, "hello");

        Assert.Equal([first, second], room.Messages);
        Assert.Equal(room.Id, first.RoomId);
        Assert.Equal(SenderType.Human, first.SenderType);
        Assert.Equal("claude", first.TargetId);
        Assert.Equal(SenderType.Agent, second.SenderType);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void AddParticipant_registers_agents_and_finds_them_case_insensitively()
    {
        var room = _rooms.CreateRoom("general");
        var participant = new AgentParticipant("claude", "Claude", new FakeSession("ok"));

        room.AddParticipant(participant);

        Assert.Same(participant, Assert.Single(room.Participants));
        Assert.Same(participant, room.FindParticipant("CLAUDE"));
        Assert.Null(room.FindParticipant("codex"));
        Assert.Equal(AgentSessionState.Ready, participant.State);
    }

    [Fact]
    public void AddParticipant_rejects_a_duplicate_id()
    {
        var room = _rooms.CreateRoom("general");
        room.AddParticipant(new AgentParticipant("claude", "Claude", session: null));

        Assert.Throws<InvalidOperationException>(
            () => room.AddParticipant(new AgentParticipant("Claude", "Other", session: null)));
    }

    [Fact]
    public async Task Create_gives_installed_agents_a_session_and_marks_missing_ones_unavailable()
    {
        var discovery = new AgentDiscoveryService([new FakeProvider("claude"), new FakeProvider("codex", installed: false)]);

        var participants = (await discovery.DiscoverAllAsync()).Select(a => AgentParticipant.Create(a, new AgentSessionOptions("."))).ToList();

        Assert.Equal(AgentSessionState.Ready, participants[0].State);
        Assert.Null(participants[1].Session);
        Assert.Equal(AgentSessionState.Unavailable, participants[1].State);
        Assert.Equal("not installed", participants[1].UnavailableReason);
    }

    [Fact]
    public async Task Discovery_reports_a_failing_provider_without_hiding_the_others()
    {
        var discovery = new AgentDiscoveryService([new FakeProvider("broken", discoveryFailure: new IOException("boom")), new FakeProvider("claude")]);

        var agents = await discovery.DiscoverAllAsync();

        Assert.False(agents[0].Result.IsInstalled);
        Assert.Contains("boom", agents[0].Result.Detail);
        Assert.True(agents[1].Result.IsInstalled);
    }
}
