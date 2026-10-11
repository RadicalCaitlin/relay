using Relay.Core.Agents;
using Relay.Core.Rooms;
using Relay.Core.Routing;

namespace Relay.Tests;

/// <summary>Roles the user gives agents, and how agents are told. See docs/adr/0003-agent-roles.md.</summary>
public class RoleTests
{
    private const string Reviewer = "The user has given you the role of reviewer in this room: You review what others propose or change.";
    private const string Withdrawn = "The user has withdrawn the role you were given earlier in this room. You have no particular role now.";

    private readonly MessageRouter _router = new();
    private readonly ChatRoom _room = new InMemoryRoomManager().CreateRoom("general");

    private (AgentParticipant Agent, FakeSession Session) AddAgent(string name, FakeSession? session = null)
    {
        session ??= new FakeSession("ok");
        var agent = new AgentParticipant(name.ToLowerInvariant(), name, session);
        _room.AddParticipant(agent);
        return (agent, session);
    }

    private static AgentRole BuiltIn(string name) => AgentRole.FindBuiltIn(name)!;

    private static AgentRole Custom(string name, string description)
    {
        Assert.True(AgentRole.TryCreate(name, description, out var role, out var error), error);
        return role;
    }

    [Fact]
    public void Built_in_roles_are_the_four_named_and_none_is_tied_to_a_provider()
    {
        Assert.Equal(["implementer", "reviewer", "architect", "coordinator"], AgentRole.BuiltIn.Select(role => role.Name));
        Assert.All(AgentRole.BuiltIn, role =>
        {
            Assert.DoesNotContain("claude", role.Description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("codex", role.Description, StringComparison.OrdinalIgnoreCase);
            Assert.InRange(role.Description.Length, 1, AgentRole.MaxDescriptionLength);
        });
        Assert.Same(BuiltIn("reviewer"), AgentRole.FindBuiltIn("Reviewer"));
        Assert.Null(AgentRole.FindBuiltIn("tester"));
    }

    [Fact]
    public void Role_of_the_users_own_is_trimmed_and_its_name_lowercased()
    {
        var role = Custom("  Tester ", "  you write a failing test first  ");

        Assert.Equal("tester", role.Name);
        Assert.Equal("you write a failing test first", role.Description);
    }

    [Theory]
    [InlineData("", "does things", "A role name is letters, digits and hyphens only.")]
    [InlineData("qa lead", "does things", "A role name is letters, digits and hyphens only.")]
    [InlineData("qa<lead>", "does things", "A role name is letters, digits and hyphens only.")]
    [InlineData("-", "does things", "A role name needs at least one letter or digit.")]
    [InlineData("---", "does things", "A role name needs at least one letter or digit.")]
    [InlineData("none", "does things", "\"none\" removes a role, so it cannot be the name of one.")]
    [InlineData("tester", "   ", "A role needs a description after the colon.")]
    public void Role_that_cannot_be_used_is_refused_with_the_reason(string name, string description, string error)
    {
        Assert.False(AgentRole.TryCreate(name, description, out var role, out var actual));
        Assert.Null(role);
        Assert.Equal(error, actual);
    }

    [Fact]
    public void Role_description_has_a_length_limit_so_it_cannot_become_a_second_prompt()
    {
        Assert.True(AgentRole.TryCreate("tester", new string('x', 500), out _, out _));
        Assert.False(AgentRole.TryCreate("tester", new string('x', 501), out _, out var error));
        Assert.Equal("A role description is at most 500 characters; this one is 501.", error);
    }

    [Fact]
    public async Task Agent_with_no_role_is_sent_the_message_as_typed()
    {
        var (_, claude) = AddAgent("Claude");
        var (codex, _) = AddAgent("Codex");
        _room.SetRole(codex, BuiltIn("reviewer"));

        await _router.SendHumanMessageAsync(_room, "@claude hello");

        Assert.Equal(["hello"], claude.Received);
    }

    [Fact]
    public async Task Agent_is_told_its_role_by_relay_on_every_turn_before_the_users_message()
    {
        var (agent, claude) = AddAgent("Claude");
        _room.SetRole(agent, BuiltIn("reviewer"));

        await _router.SendHumanMessageAsync(_room, "@claude one");
        await _router.SendHumanMessageAsync(_room, "@claude two");

        Assert.All(claude.Received, prompt =>
        {
            Assert.StartsWith("You are Claude, one participant in a shared chat room named \"general\" with a human user and other AI agents. " + Reviewer, prompt);
            Assert.Contains("unless you are asked to. The user's message to you follows this introduction.\n\n", prompt);
        });
        Assert.EndsWith("\n\none", claude.Received[0]);
        Assert.EndsWith("\n\ntwo", claude.Received[1]);
    }

    [Fact]
    public async Task Any_agent_can_hold_any_role_and_two_can_hold_the_same_one()
    {
        var (first, claude) = AddAgent("Claude");
        var (second, codex) = AddAgent("Codex");
        _room.SetRole(first, BuiltIn("reviewer"));
        _room.SetRole(second, BuiltIn("reviewer"));

        await _router.SendHumanMessageAsync(_room, "@claude one");
        await _router.SendHumanMessageAsync(_room, "@codex two");

        Assert.Contains(Reviewer, Assert.Single(claude.Received));
        Assert.Contains(Reviewer, Assert.Single(codex.Received));
    }

    [Fact]
    public async Task Changing_a_role_replaces_what_the_agent_is_told()
    {
        var (agent, claude) = AddAgent("Claude");
        _room.SetRole(agent, BuiltIn("reviewer"));
        await _router.SendHumanMessageAsync(_room, "@claude one");

        _room.SetRole(agent, BuiltIn("architect"));
        await _router.SendHumanMessageAsync(_room, "@claude two");

        Assert.Same(BuiltIn("architect"), _room.RoleOf(agent));
        Assert.Contains("the role of architect in this room: You decide structure before code is written", claude.Received[1]);
        Assert.DoesNotContain("reviewer", claude.Received[1]);
        Assert.DoesNotContain("withdrawn", claude.Received[1]);
    }

    [Fact]
    public async Task Removing_a_role_is_told_once_and_then_the_agent_is_sent_messages_as_typed_again()
    {
        var (agent, claude) = AddAgent("Claude");
        _room.SetRole(agent, BuiltIn("reviewer"));
        await _router.SendHumanMessageAsync(_room, "@claude one");

        _room.SetRole(agent, null);
        await _router.SendHumanMessageAsync(_room, "@claude two");
        await _router.SendHumanMessageAsync(_room, "@claude three");

        Assert.Null(_room.RoleOf(agent));
        Assert.Contains(Withdrawn, claude.Received[1]);
        Assert.DoesNotContain("reviewer", claude.Received[1]);
        Assert.EndsWith("\n\ntwo", claude.Received[1]);
        Assert.Equal("three", claude.Received[2]);
    }

    [Fact]
    public async Task Removal_is_told_again_when_the_turn_that_said_so_did_not_complete()
    {
        var fail = false;
        var (agent, claude) = AddAgent("Claude", new FakeSession((_, _) => fail ? Task.FromException<string>(new AgentException("down")) : Task.FromResult("ok")));
        _room.SetRole(agent, BuiltIn("reviewer"));
        await _router.SendHumanMessageAsync(_room, "@claude one");

        _room.SetRole(agent, null);
        fail = true;
        await _router.SendHumanMessageAsync(_room, "@claude two");
        fail = false;
        await _router.SendHumanMessageAsync(_room, "@claude three");

        Assert.Contains(Withdrawn, claude.Received[1]);
        Assert.Contains(Withdrawn, claude.Received[2]);
    }

    [Fact]
    public async Task Removing_a_role_an_agent_never_had_tells_it_nothing()
    {
        var (agent, claude) = AddAgent("Claude");

        _room.SetRole(agent, null);
        await _router.SendHumanMessageAsync(_room, "@claude hello");

        Assert.False(_room.RoleWithdrawn(agent));
        Assert.Equal(["hello"], claude.Received);
    }

    [Fact]
    public async Task Removing_a_role_the_agent_was_never_sent_tells_it_nothing()
    {
        var (agent, claude) = AddAgent("Claude");

        _room.SetRole(agent, BuiltIn("reviewer"));
        _room.SetRole(agent, null);
        await _router.SendHumanMessageAsync(_room, "@claude hello");

        Assert.False(_room.RoleWithdrawn(agent));
        Assert.Equal(["hello"], claude.Received);
    }

    [Fact]
    public async Task Removal_is_told_when_the_only_turn_that_stated_the_role_did_not_complete()
    {
        var fail = true;
        var (agent, claude) = AddAgent("Claude", new FakeSession((_, _) => fail ? Task.FromException<string>(new AgentException("down")) : Task.FromResult("ok")));
        _room.SetRole(agent, BuiltIn("reviewer"));
        await _router.SendHumanMessageAsync(_room, "@claude one");

        _room.SetRole(agent, null);
        fail = false;
        await _router.SendHumanMessageAsync(_room, "@claude two");

        Assert.Contains(Reviewer, claude.Received[0]);
        Assert.Contains(Withdrawn, claude.Received[1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Role_outlives_a_new_conversation_which_also_makes_an_earlier_removal_moot(bool keepRoom)
    {
        var (first, claude) = AddAgent("Claude");
        var (second, codex) = AddAgent("Codex");
        _room.SetRole(first, BuiltIn("reviewer"));
        _room.SetRole(second, BuiltIn("architect"));
        await _router.SendHumanMessageAsync(_room, "@codex first");
        _room.SetRole(second, null);

        foreach (var agent in new[] { first, second })
        {
            if (keepRoom)
                _room.RestartConversation(agent);
            else
                _room.ResetConversation(agent);
        }

        await _router.SendHumanMessageAsync(_room, "@claude hello");
        await _router.SendHumanMessageAsync(_room, "@codex hello");

        Assert.Contains(Reviewer, Assert.Single(claude.Received));
        Assert.DoesNotContain("withdrawn", codex.Received[^1]);
    }

    [Fact]
    public async Task Agents_in_an_exchange_are_told_each_others_roles()
    {
        var (first, claude) = AddAgent("Claude", new FakeSession("@codex\nplease review"));
        var (second, codex) = AddAgent("Codex");
        AddAgent("Gemini");
        _room.SetRole(first, BuiltIn("implementer"));
        _room.SetRole(second, BuiltIn("reviewer"));

        await _router.SendHumanMessageAsync(_room, "@claude @codex add retry");

        const string agents = "The user has opened this exchange to these agents: Claude (@claude, implementer), Codex (@codex, reviewer).";
        Assert.Contains(agents, Assert.Single(claude.Received));
        var handedOver = Assert.Single(codex.Received);
        Assert.Contains(agents, handedOver);
        Assert.Contains(Reviewer, handedOver);
        Assert.EndsWith("Claude addressed the last message above to you.", handedOver);
    }

    [Fact]
    public async Task Role_text_of_the_users_own_is_escaped_and_ends_like_a_sentence()
    {
        var (agent, claude) = AddAgent("Claude");
        _room.SetRole(agent, Custom("tester", "write a <failing> test \"first\"\nthen fix"));

        await _router.SendHumanMessageAsync(_room, "@claude hello");

        var prompt = Assert.Single(claude.Received);
        Assert.Contains("the role of tester in this room: write a &lt;failing&gt; test &quot;first&quot;&#10;then fix. The user's message to you follows", prompt);
        Assert.EndsWith("\n\nhello", prompt);
    }

    [Fact]
    public void Roles_belong_to_the_room_an_agent_is_in()
    {
        var stranger = new AgentParticipant("gemini", "Gemini", new FakeSession("x"));

        Assert.Throws<InvalidOperationException>(() => _room.SetRole(stranger, BuiltIn("reviewer")));
        Assert.Throws<InvalidOperationException>(() => _room.RoleOf(stranger));
        Assert.Throws<InvalidOperationException>(() => _room.RoleWithdrawn(stranger));
    }

    [Fact]
    public void Unavailable_agent_can_be_given_a_role()
    {
        var agent = new AgentParticipant("codex", "Codex", session: null, "not installed");
        _room.AddParticipant(agent);

        _room.SetRole(agent, BuiltIn("reviewer"));

        Assert.Same(BuiltIn("reviewer"), _room.RoleOf(agent));
    }
}
