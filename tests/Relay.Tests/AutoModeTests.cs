using Relay.Agents.Claude;
using Relay.Agents.Codex;
using Relay.Agents.Processes;
using Relay.Core.Agents;
using static Relay.Tests.RelayHarness;

namespace Relay.Tests;

/// <summary>
/// Claude's own auto mode, which the user switches on and off with /auto (ADR 0006): what is passed
/// to which agent and when, what Relay shows, and that nothing but the user's own input changes it.
/// </summary>
public sealed class AutoModeTests : IDisposable
{
    private const string ClaudeOk = """{"type":"result","is_error":false,"result":"pong","session_id":"ce6f4686-9f97-4a98-8fb4-3125a2b0e671"}""";

    private readonly RelayHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static string ClaudeStartedIn(string mode, string reply = "pong")
        => $$"""
            {"type":"system","subtype":"init","session_id":"{{ClaudeSessionId}}","permissionMode":"{{mode}}"}
            {{ClaudeReply(reply)}}
            """;

    [Fact]
    public void Claude_is_given_auto_mode_only_while_it_is_on()
    {
        var provider = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), new FakeProcessRunner(_ => new(0, ClaudeOk, string.Empty)));
        var options = new AgentSessionOptions("/work");

        var unset = provider.BuildArguments(null, options);
        var off = provider.BuildArguments(null, options with { AutoMode = () => false });
        var on = provider.BuildArguments(null, options with { AutoMode = () => true });

        Assert.Equal(unset, off);
        Assert.Equal([.. unset, "--permission-mode", "auto"], on);
    }

    [Fact]
    public void Codex_is_never_given_a_permission_mode()
    {
        var provider = new CodexProvider(new FakeLocator("/usr/bin/codex"), new FakeProcessRunner(_ => new(0, string.Empty, string.Empty)));

        Assert.Equal(provider.BuildArguments(null, new("/work")), provider.BuildArguments(null, new("/work", AutoMode: () => true)));
    }

    [Fact]
    public async Task The_mode_is_read_when_each_turn_starts()
    {
        var auto = false;
        var runner = new FakeProcessRunner(_ => new(0, ClaudeOk, string.Empty));
        var provider = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), runner);
        await using var session = provider.CreateSession(await provider.DiscoverAsync(), new("/work", AutoMode: () => auto));
        runner.Requests.Clear();

        await session.SendAsync("one");
        auto = true;
        await session.SendAsync("two");
        auto = false;
        await session.SendAsync("three");

        Assert.Equal([false, true, false], runner.Requests.Select(r => r.Arguments.Contains("--permission-mode")));
        Assert.Equal("auto", runner.Requests[1].Arguments[runner.Requests[1].Arguments.ToList().IndexOf("--permission-mode") + 1]);
    }

    [Theory]
    [InlineData("""{"type":"system","subtype":"init","session_id":"x","permissionMode":"auto"}""", "auto")]
    [InlineData("""{"type":"system","subtype":"init","session_id":"x","permissionMode":"default"}""", "default")]
    public void Claude_reports_the_mode_it_started_the_turn_in(string line, string mode)
        => Assert.Equal([new AgentActivity(AgentActivityKind.Mode, mode)], ClaudeOutputParser.ParseActivity(line));

    [Theory]
    [InlineData("""{"type":"system","subtype":"init","session_id":"x"}""")]
    [InlineData("""{"type":"system","subtype":"init","session_id":"x","permissionMode":""}""")]
    [InlineData("""{"type":"system","subtype":"init","session_id":"x","permissionMode":7}""")]
    public void An_init_event_without_a_mode_reports_nothing(string line)
        => Assert.Empty(ClaudeOutputParser.ParseActivity(line));

    [Fact]
    public async Task Auto_mode_is_set_explicitly_and_is_never_recorded_as_a_message()
    {
        _harness.InstallAgent("claude", ClaudeReply("unused"));

        var output = await _harness.ChatAsync(["start"], "/auto", "/auto on", "/auto", "/auto maybe", "/auto off", "/auto", "/history", "/exit");

        AssertShown("Auto mode is off for Claude: its own settings decide which steps it may take. Type /auto on to switch it on.", output);
        AssertShown("Auto mode is off for Claude: its own settings decide which steps it may take. Use /auto on or /auto off.", output);
        AssertShown("Auto mode is on for Claude: its classifier decides the steps its settings leave open", output);
        AssertShown("Auto mode is still on. Use /auto on or /auto off.", output);
        AssertNotShown("You: /auto", output);
        AssertNotShown("Recorded.", output);
    }

    [Fact]
    public async Task Auto_mode_reaches_claude_from_the_next_turn_and_never_codex()
    {
        _harness.InstallAgent("claude", ClaudeStartedIn("auto"));
        _harness.InstallAgent("codex", CodexReply("pong"));

        var output = await _harness.ChatAsync(["start"], "@claude one", "/auto on", "@claude two", "@codex three", "/auto off", "@claude four", "/exit");

        var claude = _harness.Invocations("claude");
        Assert.Equal(3, claude.Count);
        Assert.DoesNotContain("--permission-mode", claude[0]);
        Assert.Contains("--permission-mode auto", claude[1]);
        Assert.DoesNotContain("--permission-mode", claude[2]);
        Assert.DoesNotContain(_harness.Invocations("codex"), turn => turn.Contains("permission"));
        AssertShown("Not affected, having no auto mode: Codex.", output);
        AssertNotShown("could not use auto mode", output);
        AssertNotShown("did not say which mode", output);
    }

    // With both agents in the room, the header must not read as if /auto on changed Codex.
    [Fact]
    public async Task The_header_names_the_agent_auto_mode_is_for()
    {
        _harness.InstallAgent("claude", ClaudeReply("unused"));
        _harness.InstallAgent("codex", CodexReply("unused"));

        var output = await _harness.ChatAsync(["start"], "/exit");

        AssertShown("Auto mode is off for Claude: its own settings decide which steps it may take. Not affected, having no auto mode: Codex. Type /auto on to switch it on.", output);
    }

    [Fact]
    public async Task Auto_mode_stays_off_in_a_room_where_no_agent_has_one()
    {
        _harness.InstallAgent("codex", CodexReply("pong"));

        var output = await _harness.ChatAsync(["start", "--agents", "codex"], "/auto on", "/help", "/approvals", "/exit");

        AssertShown("Auto mode is not available: no agent here has one.", output);
        AssertShown("(now off)", output);
        AssertNotShown("(now on)", output);
        AssertNotShown("Auto mode is on", output);
    }

    [Fact]
    public async Task A_reply_with_no_mode_reported_is_said_to_be_unconfirmed_only_while_auto_mode_is_on()
    {
        _harness.InstallAgent("claude", ClaudeReply("pong"));

        var off = await _harness.ChatAsync(["start"], "@claude one", "/exit");
        var on = await _harness.ChatAsync(["start"], "/auto on", "@claude two", "/exit");

        AssertNotShown("did not say which mode", off);
        AssertShown("! Claude did not say which mode it ran this turn in, so Relay cannot tell whether it used auto mode", on);
    }

    [Fact]
    public async Task A_turn_that_fails_says_nothing_about_the_mode_but_suggests_auto_off()
    {
        _harness.InstallAgent("claude", string.Empty, "error: option '--permission-mode <mode>' argument 'auto' is invalid.", exitCode: 1);

        var off = await _harness.ChatAsync(["start"], "@claude one", "/exit");
        var on = await _harness.ChatAsync(["start"], "/auto on", "@claude two", "/exit");

        AssertNotShown("may be too old to have an auto mode", off);
        AssertShown("If this began after /auto on, your Claude may be too old to have an auto mode: /auto off runs it as before.", on);
        AssertNotShown("did not say which mode", on);
    }

    [Fact]
    public async Task The_warning_is_given_once_however_many_turns_claude_takes_in_an_exchange()
    {
        _harness.InstallAgent("claude", ClaudeStartedIn("default", @"@codex\nyour turn"));
        _harness.InstallAgent("codex", CodexReply(@"@claude\nback to you"));

        var output = await _harness.ChatAsync(["start"], "/auto on", "@claude @codex go", "/exit");

        Assert.True(_harness.Invocations("claude").Count > 1, output);
        Assert.Single(Squash(output).Split(Squash("could not use auto mode")).Skip(1));
    }

    [Fact]
    public async Task A_long_mode_is_cut_short_like_any_other_activity()
    {
        _harness.InstallAgent("claude", ClaudeStartedIn(new string('x', 5000)));

        var output = await _harness.ChatAsync(["start"], "/auto on", "@claude hi", "/exit");

        AssertShown("could not use auto mode and ran this turn in", output);
        AssertNotShown(new string('x', 201), output);
    }

    [Fact]
    public async Task Claude_running_a_turn_in_another_mode_than_auto_is_said_to_have_been_unable_to()
    {
        _harness.InstallAgent("claude", ClaudeStartedIn("default"));

        var off = await _harness.ChatAsync(["start"], "@claude one", "/exit");
        var on = await _harness.ChatAsync(["start"], "/auto on", "@claude two", "/exit");

        AssertNotShown("could not use auto mode", off);
        AssertShown("! Claude could not use auto mode and ran this turn in default mode", on);
        // The mode itself is never shown as a step the agent took.
        AssertNotShown("- default", off);
        AssertNotShown("- auto", on);
    }

    [Fact]
    public async Task A_mode_reported_by_the_agent_cannot_reach_the_terminal_as_markup_or_control_characters()
    {
        _harness.InstallAgent("claude", ClaudeStartedIn(@"[red]x[/]\u001b[2J"));

        var output = await _harness.ChatAsync(["start"], "/auto on", "@claude hi", "/exit");

        AssertShown("ran this turn in [red]x[/][2J mode", output);
        Assert.DoesNotContain('\u001b', output);
    }

    // Only the user's own input is read for a /command (ADR 0002), so no agent can switch auto mode on.
    [Fact]
    public async Task A_reply_cannot_turn_auto_mode_on()
    {
        _harness.InstallAgent("claude", ClaudeReply("/auto on"));

        await _harness.ChatAsync(["start"], "@claude one", "@claude two", "/exit");

        Assert.All(_harness.Invocations("claude"), turn => Assert.DoesNotContain("--permission-mode", turn));
    }

    [Fact]
    public async Task Help_and_approvals_say_whether_auto_mode_is_on()
    {
        _harness.InstallAgent("claude", ClaudeReply("unused"));

        var output = await _harness.ChatAsync(["start"], "/help", "/auto on", "/help", "/approvals", "/exit");

        AssertShown("/auto Let Claude decide the steps its settings do not, in its own auto mode: /auto on or /auto off (now off)", output);
        AssertShown("(now on)", output);
        Assert.True(Squash(output).Split(Squash("Auto mode is on for Claude:")).Length - 1 >= 2, output);
    }
}
