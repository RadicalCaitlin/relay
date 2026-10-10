using static Relay.Tests.RelayHarness;

namespace Relay.Tests;

/// <summary>
/// Smoke tests that run the compiled <c>relay</c> CLI against fake <c>claude</c> and <c>codex</c>
/// executables: discovery, the chatroom, routing, failures and exit codes. No real agent, account
/// or network is involved, whether or not the real CLIs are installed on the machine.
/// </summary>
public sealed class CliSmokeTests : IDisposable
{
    private readonly RelayHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private void InstallBothAgents()
    {
        _harness.InstallAgent("claude", ClaudeReply("hello from claude"));
        _harness.InstallAgent("codex", CodexReply("hello from codex"));
    }

    [Fact]
    public async Task Home_screen_shows_the_banner_agent_status_and_commands()
    {
        _harness.InstallAgent("claude", ClaudeReply("unused"));

        var output = await _harness.ChatAsync([]);

        AssertShown("RELAY", output);
        AssertShown("Your agents. One conversation.", output);
        AssertShown("Claude installed", output);
        AssertShown("Codex not found", output);
        AssertShown("relay start", output);
        AssertShown("relay agents", output);
        Assert.Empty(_harness.Invocations("claude"));
    }

    [Fact]
    public async Task Version_prints_a_version_number()
    {
        var output = await _harness.ChatAsync(["--version"]);

        Assert.Matches(@"^\d+\.\d+\.\d+", output.Trim());
    }

    [Fact]
    public async Task Help_lists_every_command()
    {
        var output = await _harness.ChatAsync(["--help"]);

        AssertShown("agents", output);
        AssertShown("start", output);
        AssertShown("trust", output);
    }

    [Fact]
    public async Task Agents_lists_installed_agents_with_their_versions()
    {
        InstallBothAgents();

        var output = await _harness.ChatAsync(["agents"]);

        AssertShown("@claude", output);
        AssertShown("@codex", output);
        AssertShown("9.9.9 (fake claude)", output);
        AssertShown("9.9.9 (fake codex)", output);
        AssertNotShown("not found", output);
        Assert.Empty(_harness.Invocations("claude"));
        Assert.Empty(_harness.Invocations("codex"));
    }

    [Fact]
    public async Task Agents_reports_missing_agents_without_failing()
    {
        var output = await _harness.ChatAsync(["agents"]);

        AssertShown("not found", output);
        AssertShown("'claude' was not found on PATH", output);
        AssertShown("'codex' was not found on PATH", output);
        AssertNotShown("installed", output);
    }

    [Fact]
    public async Task Unknown_command_or_option_fails_with_a_nonzero_exit_code()
    {
        var (unknownCommand, commandOutput) = await _harness.RunAsync(["frobnicate"]);
        var (unknownOption, optionOutput) = await _harness.RunAsync(["start", "--no-such-option"], "/exit");

        Assert.NotEqual(0, unknownCommand);
        AssertShown("Unknown command 'frobnicate'", commandOutput);
        Assert.NotEqual(0, unknownOption);
        AssertShown("Unknown option 'no-such-option'", optionOutput);
        AssertNotShown("# general", optionOutput);
    }

    [Fact]
    public async Task Start_shows_the_room_and_leaves_cleanly_on_exit()
    {
        _harness.InstallAgent("claude", ClaudeReply("unused"));

        var output = await _harness.ChatAsync(["start"], "/exit", "@claude this line is never read");

        AssertShown("Claude connected", output);
        AssertShown("Codex unavailable", output);
        AssertShown("# general", output);
        Assert.Empty(_harness.Invocations("claude"));
    }

    [Fact]
    public async Task Start_leaves_cleanly_when_input_ends_without_exit()
    {
        _harness.InstallAgent("claude", ClaudeReply("pong"));

        var output = await _harness.ChatAsync(["start"], "@claude hi");

        AssertShown("Claude: pong", output);
    }

    [Fact]
    public async Task Start_works_with_no_agents_installed()
    {
        var output = await _harness.ChatAsync(["start"], "@claude hi", "/exit");

        AssertShown("Claude unavailable", output);
        AssertShown("Codex unavailable", output);
        AssertShown("Claude is unavailable: 'claude' was not found on PATH", output);
    }

    [Fact]
    public async Task Targeted_messages_reach_the_named_agent_and_its_reply_is_shown()
    {
        InstallBothAgents();

        var output = await _harness.ChatAsync(["start"], "@claude first question", "@codex second question", "/exit");

        AssertShown("Claude: hello from claude", output);
        AssertShown("Codex: hello from codex", output);
        Assert.Contains("first question", _harness.StandardInput("claude"));
        Assert.Contains("second question", _harness.StandardInput("codex"));
        Assert.Equal("-p --output-format json --setting-sources user", Assert.Single(_harness.Invocations("claude")));
        Assert.Equal("exec --json -", Assert.Single(_harness.Invocations("codex")));
    }

    [Fact]
    public async Task Follow_up_messages_continue_each_agents_conversation_until_reset()
    {
        InstallBothAgents();

        await _harness.ChatAsync(["start"], "@claude one", "@claude two", "@codex one", "@codex two", "/reset claude", "@claude three", "/exit");

        var claude = _harness.Invocations("claude");
        var codex = _harness.Invocations("codex");
        Assert.Equal(3, claude.Count);
        Assert.DoesNotContain("--resume", claude[0]);
        Assert.EndsWith($"--resume {ClaudeSessionId}", claude[1]);
        Assert.DoesNotContain("--resume", claude[2]);
        Assert.Equal(["exec --json -", $"exec resume --json {CodexThreadId} -"], codex);
    }

    [Fact]
    public async Task Messages_that_address_nobody_or_an_unknown_agent_are_explained_and_sent_nowhere()
    {
        InstallBothAgents();

        var output = await _harness.ChatAsync(["start"], "hello everyone", "@gemini hi", "@claude", "/frobnicate", "/exit");

        AssertShown("Recorded. Address an agent to get a reply", output);
        AssertShown("No agent named @gemini in this room. Agents here: @claude, @codex.", output);
        AssertShown("Nothing to send. Try: @claude <message>", output);
        AssertShown("Unknown command /frobnicate. Type /help.", output);
        Assert.Empty(_harness.Invocations("claude"));
        Assert.Empty(_harness.Invocations("codex"));
    }

    [Fact]
    public async Task Chat_commands_show_help_agents_and_history_and_clear_keeps_the_history()
    {
        _harness.InstallAgent("claude", ClaudeReply("pong"));

        var output = await _harness.ChatAsync(["start"], "/help", "/agents", "@claude ping", "/clear", "/history", "/exit");

        AssertShown("/reset", output);
        AssertShown("@claude <message>", output);
        AssertShown("9.9.9 (fake claude)", output);
        // Once when the room opens and once more when /clear redraws it.
        Assert.Equal(2, output.Split("# general").Length - 1);
        var afterClear = output[output.LastIndexOf("# general", StringComparison.Ordinal)..];
        AssertShown("You: @claude ping", afterClear);
        AssertShown("Claude: pong", afterClear);
    }

    [Fact]
    public async Task Agent_that_exits_with_an_error_is_reported_and_the_chat_carries_on()
    {
        _harness.InstallAgent("claude", stdout: "", stderr: "fatal: could not reach the service\n", exitCode: 3);

        var output = await _harness.ChatAsync(["start"], "@claude hi", "hello again", "/exit");

        AssertShown("Claude failed: claude exited with code 3: fatal: could not reach the service", output);
        AssertShown("/reset claude starts a new conversation", output);
        AssertShown("Recorded.", output);
    }

    [Fact]
    public async Task Agent_error_message_is_shown_as_the_agent_reported_it()
    {
        _harness.InstallAgent("claude", """{"type":"result","is_error":true,"result":"Not logged in. Please run /login"}""", exitCode: 1);
        _harness.InstallAgent("codex", """{"type":"turn.failed","error":{"message":"usage limit reached"}}""", exitCode: 1);

        var output = await _harness.ChatAsync(["start"], "@claude hi", "@codex hi", "/exit");

        AssertShown("Claude failed: Not logged in. Please run /login", output);
        AssertShown("Codex failed: usage limit reached", output);
    }

    [Fact]
    public async Task Agent_that_prints_something_unexpected_is_reported_as_a_failure()
    {
        _harness.InstallAgent("claude", "this is not json\n");
        _harness.InstallAgent("codex", "this is not json\n");

        var output = await _harness.ChatAsync(["start"], "@claude hi", "@codex hi", "/exit");

        AssertShown("Claude failed: claude finished without an error, but Relay could not read its output.", output);
        AssertShown("Codex failed: codex finished without an error, but Relay could not read its output.", output);
        AssertNotShown("this is not json", output);
    }

    [Fact]
    public async Task Agent_that_is_uninstalled_mid_session_is_reported_and_the_chat_carries_on()
    {
        _harness.InstallAgent("claude", ClaudeReply("pong"));
        var relay = _harness.Start("start");
        await relay.StandardInput.WriteLineAsync("@claude one");
        await Poll.UntilAsync(() => Squash(_harness.Output).Contains(Squash("Claude: pong")), "the first reply is shown");

        File.Delete(_harness.PathTo(OperatingSystem.IsWindows() ? "claude.cmd" : "claude"));
        await relay.StandardInput.WriteLineAsync("@claude two");
        await relay.StandardInput.WriteLineAsync("still here");
        await relay.StandardInput.WriteLineAsync("/exit");
        await relay.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(0, relay.ExitCode);
        AssertShown("Claude failed: Could not start 'claude':", _harness.Output);
        AssertShown("check that Claude works when you run it on its own", _harness.Output);
        AssertShown("Recorded.", _harness.Output);
    }

    [Fact]
    public async Task Agent_whose_version_check_fails_is_still_listed_and_usable()
    {
        _harness.InstallAgentScript(
            "claude",
            unixScript: "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo broken >&2; exit 1; fi\ncat > /dev/null\necho '{\"is_error\":false,\"result\":\"pong\"}'\n",
            windowsScript: "@echo off\nif \"%~1\"==\"--version\" (echo broken 1>&2 & exit /b 1)\necho {\"is_error\":false,\"result\":\"pong\"}\n");

        var agents = await _harness.ChatAsync(["agents"]);
        var chat = await _harness.ChatAsync(["start"], "@claude hi", "/exit");

        AssertShown("installed", agents);
        AssertNotShown("broken", agents);
        AssertShown("Claude: pong", chat);
    }

    [Fact]
    public async Task Agent_recovers_after_a_failure_and_keeps_its_conversation()
    {
        _harness.InstallAgent("claude", ClaudeReply("pong"));

        // One session: a good turn, then the agent starts failing, then it works again.
        var relay = _harness.Start("start");
        await relay.StandardInput.WriteLineAsync("@claude one");
        await Poll.UntilAsync(() => _harness.Invocations("claude").Count == 1, "the first turn is sent");
        await Poll.UntilAsync(() => Squash(_harness.Output).Contains(Squash("Claude: pong")), "the first reply is shown");

        _harness.SetAgentOutput("claude", stdout: "", stderr: "rate limited\n", exitCode: 1);
        await relay.StandardInput.WriteLineAsync("@claude two");
        await Poll.UntilAsync(() => Squash(_harness.Output).Contains(Squash("Claude failed:")), "the failure is shown");

        _harness.SetAgentOutput("claude", ClaudeReply("back again"));
        await relay.StandardInput.WriteLineAsync("@claude three");
        await relay.StandardInput.WriteLineAsync("/exit");
        await relay.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(0, relay.ExitCode);
        AssertShown("Claude: back again", _harness.Output);
        var turns = _harness.Invocations("claude");
        Assert.Equal(3, turns.Count);
        Assert.EndsWith($"--resume {ClaudeSessionId}", turns[1]);
        Assert.EndsWith($"--resume {ClaudeSessionId}", turns[2]);
    }
}
