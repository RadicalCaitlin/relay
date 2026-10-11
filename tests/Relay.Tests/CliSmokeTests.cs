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
        Assert.Equal("-p --output-format stream-json --verbose --setting-sources user", Assert.Single(_harness.Invocations("claude")));
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
    public async Task Each_agent_is_shown_what_the_others_were_told_and_what_they_answered()
    {
        InstallBothAgents();

        await _harness.ChatAsync(["start"], "@codex the codeword is banana", "a note for the room", "@claude what is the codeword?", "/exit");

        var sent = _harness.StandardInput("claude");
        Assert.Contains("""<message from="User" kind="human" to="Codex">@codex the codeword is banana</message>""", sent);
        Assert.Contains("""<message from="Codex" kind="agent" to="User">hello from codex</message>""", sent);
        Assert.Contains("""<message from="User" kind="human" to="everyone">a note for the room</message>""", sent);
        Assert.EndsWith("what is the codeword?", sent.TrimEnd());
        Assert.DoesNotContain("room-transcript", _harness.StandardInput("codex"));
    }

    [Fact]
    public async Task Agents_named_together_pass_a_message_between_them_and_each_reply_is_shown()
    {
        _harness.InstallAgent("claude", ClaudeReply(@"@codex\nwhat do you think?"));
        _harness.InstallAgent("codex", CodexReply("fine by me"));

        var output = await _harness.ChatAsync(["start"], "@claude @codex agree on a plan", "/exit");

        AssertShown("Waiting for Claude... (turn 1 of 6, Ctrl+C to stop the exchange)", output);
        AssertShown("Claude to Codex: @codex what do you think?", output);
        AssertShown("Waiting for Codex... (turn 2 of 6, Ctrl+C to stop the exchange)", output);
        AssertNotShown("not delivered", output);
        AssertShown("Codex: fine by me", output);
        Assert.Single(_harness.Invocations("claude"));
        Assert.Single(_harness.Invocations("codex"));
        var sent = _harness.StandardInput("codex");
        Assert.Contains("""<message from="User" kind="human" to="Claude">@claude @codex agree on a plan</message>""", sent);
        Assert.Contains("""<message from="Claude" kind="agent" to="Codex">@codex&#10;what do you think?</message>""", sent);
        Assert.EndsWith("Claude addressed the last message above to you.", sent.TrimEnd());
    }

    [Fact]
    public async Task Agent_that_addresses_an_agent_the_user_did_not_name_starts_no_turn()
    {
        _harness.InstallAgent("claude", ClaudeReply(@"@codex\ntake over"));
        _harness.InstallAgent("codex", CodexReply("unused"));

        var output = await _harness.ChatAsync(["start"], "@claude please ask @codex about this", "@claude @Override is missing here", "/exit");

        AssertShown("Claude: @codex take over", output);
        AssertNotShown("Claude to Codex", output);
        AssertNotShown("No agent named", output);
        Assert.EndsWith("@Override is missing here", _harness.StandardInput("claude").TrimEnd());
        Assert.Equal(2, _harness.Invocations("claude").Count);
        Assert.Empty(_harness.Invocations("codex"));
    }

    [Fact]
    public async Task Reply_for_the_user_that_begins_by_naming_the_other_agent_ends_the_exchange()
    {
        _harness.InstallAgent("claude", ClaudeReply("@codex and I would both use a channel."));
        _harness.InstallAgent("codex", CodexReply("unused"));

        var output = await _harness.ChatAsync(["start"], "@claude @codex agree on a plan", "/exit");

        AssertShown("Claude: @codex and I would both use a channel.", output);
        Assert.Empty(_harness.Invocations("codex"));
    }

    [Fact]
    public async Task Agents_that_keep_addressing_each_other_are_stopped_after_six_turns()
    {
        _harness.InstallAgent("claude", ClaudeReply(@"@codex\nand you?"));
        _harness.InstallAgent("codex", CodexReply(@"@claude\nand you?"));

        var output = await _harness.ChatAsync(["start"], "@claude @codex discuss", "/exit");

        AssertShown("Stopped after 6 agent turns, the limit for one exchange. Codex's last message was addressed to Claude and was not delivered.", output);
        Assert.Equal(3, _harness.Invocations("claude").Count);
        Assert.Equal(3, _harness.Invocations("codex").Count);
    }

    [Fact]
    public async Task Pause_mode_holds_a_handover_until_go_and_an_empty_line_does_not_send_it()
    {
        _harness.InstallAgent("claude", ClaudeReply(@"@codex\nwhat do you think?"));
        _harness.InstallAgent("codex", CodexReply("fine by me"));

        var output = await _harness.ChatAsync(["start"], "/pause on", "@claude @codex agree", "", "/go", "/exit");

        AssertShown("Pause mode is on:", output);
        AssertShown("Paused. /go sends this to Codex, /stop ends the exchange, anything else goes back to Claude as your message.", output);
        AssertShown("Codex: fine by me", output);
        Assert.Equal(2, output.Split("Paused.").Length - 1);
        Assert.Single(_harness.Invocations("codex"));
    }

    [Fact]
    public async Task Near_miss_commands_at_a_pause_are_refused_and_never_sent_to_an_agent()
    {
        _harness.InstallAgent("claude", ClaudeReply(@"@codex\nwhat do you think?"));
        _harness.InstallAgent("codex", CodexReply("fine by me"));

        var output = await _harness.ChatAsync(["start"], "/pause on", "@claude @codex agree", "/go ahead", "/stop now", "/go.", "/history", "@codex go on", "/go", "/exit");

        AssertShown("/go ahead is not a command here.", output);
        AssertShown("/stop now is not a command here.", output);
        AssertShown("/go. is not a command here.", output);
        AssertShown("/history is not a command here.", output);
        AssertShown("What you type here goes to Claude, not to Codex. Type /go to send Claude's message on.", output);
        AssertShown("Codex: fine by me", output);
        Assert.Single(_harness.Invocations("claude"));
        Assert.Single(_harness.Invocations("codex"));
    }

    [Fact]
    public async Task Pause_is_set_explicitly_and_is_never_recorded_as_a_message()
    {
        InstallBothAgents();

        var output = await _harness.ChatAsync(["start"], "/pause", "/pause on", "/pause", "/pause maybe", "/pause off", "/pause", "/history", "/exit");

        AssertShown("Pause mode is off. Use /pause on or /pause off.", output);
        AssertShown("Pause mode is on: when one agent addresses another", output);
        AssertShown("Pause mode is on. Use /pause on or /pause off.", output);
        AssertShown("Pause mode is still on. Use /pause on or /pause off.", output);
        AssertShown("Pause mode is off: agents you name together", output);
        AssertNotShown("You: /pause", output);
        AssertNotShown("Recorded.", output);
    }

    [Fact]
    public async Task Help_only_suggests_an_exchange_between_agents_that_are_available()
    {
        _harness.InstallAgent("claude", ClaudeReply("unused"));

        var oneAgent = await _harness.ChatAsync(["start"], "/help", "/exit");
        _harness.InstallAgent("codex", CodexReply("unused"));
        var twoAgents = await _harness.ChatAsync(["start"], "/help", "/exit");

        AssertNotShown("@claude @codex <message>", oneAgent);
        AssertShown("@claude @codex <message>", twoAgents);
    }

    [Fact]
    public async Task Stopping_at_a_pause_or_running_out_of_input_delivers_nothing()
    {
        _harness.InstallAgent("claude", ClaudeReply(@"@codex\nwhat do you think?"));
        _harness.InstallAgent("codex", CodexReply("unused"));

        var stopped = await _harness.ChatAsync(["start"], "/pause on", "@claude @codex agree", "/stop", "/exit");
        var inputEnded = await _harness.ChatAsync(["start"], "/pause on", "@claude @codex agree");

        AssertShown("Stopped. Claude's message to Codex was not delivered.", stopped);
        AssertShown("Stopped. Claude's message to Codex was not delivered.", inputEnded);
        Assert.Empty(_harness.Invocations("codex"));
    }

    [Fact]
    public async Task Text_typed_at_a_pause_goes_back_to_the_author_and_not_to_the_other_agent()
    {
        _harness.InstallAgent("claude", ClaudeReply(@"@codex\nwhat do you think?"));
        _harness.InstallAgent("codex", CodexReply("unused"));

        await _harness.ChatAsync(["start"], "/pause on", "@claude @codex agree", "make it shorter", "/stop", "/exit");

        Assert.Equal(2, _harness.Invocations("claude").Count);
        Assert.EndsWith("make it shorter", _harness.StandardInput("claude").TrimEnd());
        Assert.Empty(_harness.Invocations("codex"));
    }

    [Fact]
    public async Task Restarted_agent_starts_a_new_conversation_but_is_shown_the_room_again()
    {
        InstallBothAgents();

        var output = await _harness.ChatAsync(["start"], "@claude the codeword is banana", "/restart claude", "@claude what is the codeword?", "/exit");

        var turns = _harness.Invocations("claude");
        Assert.Equal(2, turns.Count);
        Assert.DoesNotContain("--resume", turns[1]);
        var sent = _harness.StandardInput("claude");
        Assert.Contains("""<message from="User" kind="human" to="Claude">@claude the codeword is banana</message>""", sent);
        Assert.Contains("""<message from="Claude" kind="agent" to="User">hello from claude</message>""", sent);
        AssertShown("will be shown the recent messages in this room again", output);
    }

    [Fact]
    public async Task Reset_agent_is_not_shown_what_was_said_before_the_reset()
    {
        InstallBothAgents();

        var output = await _harness.ChatAsync(["start"], "@codex the codeword is banana", "/reset claude", "@claude what is the codeword?", "/exit");

        Assert.Equal("what is the codeword?", _harness.StandardInput("claude").Trim());
        AssertShown("will not be shown anything said before now", output);
    }

    [Fact]
    public async Task Messages_that_address_nobody_or_an_unknown_agent_are_explained_and_sent_nowhere()
    {
        InstallBothAgents();

        var output = await _harness.ChatAsync(["start"], "hello everyone", "@gemini hi", "@claude", "/frobnicate", "/exit");

        AssertShown("Recorded. Agents will see it the next time you address them", output);
        AssertShown("No agent named @gemini in this room. Agents here: @claude, @codex.", output);
        AssertShown("Nothing to send. Try: @claude <message>", output);
        AssertShown("Unknown command /frobnicate. Type /help.", output);
        Assert.Empty(_harness.Invocations("claude"));
        Assert.Empty(_harness.Invocations("codex"));
    }

    [Fact]
    public async Task Role_given_to_an_agent_reaches_it_with_every_message_until_it_is_removed()
    {
        InstallBothAgents();

        var relay = _harness.Start("start");
        await relay.StandardInput.WriteLineAsync("/role claude reviewer");
        await relay.StandardInput.WriteLineAsync("@claude look at this");
        await Poll.UntilAsync(() => _harness.Invocations("claude").Count == 1 && Squash(_harness.Output).Contains(Squash("Claude: hello from claude")), "the first reply is shown");
        var withRole = _harness.StandardInput("claude");

        await relay.StandardInput.WriteLineAsync("/role @claude none");
        await relay.StandardInput.WriteLineAsync("@claude and this");
        await Poll.UntilAsync(() => _harness.Invocations("claude").Count == 2, "the second turn is sent");
        await relay.StandardInput.WriteLineAsync("/exit");
        await relay.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Contains("The user has given you the role of reviewer in this room: You review what others propose or change.", withRole);
        Assert.EndsWith("look at this", withRole.TrimEnd());
        Assert.Contains("The user has withdrawn the role you were given earlier in this room.", _harness.StandardInput("claude"));
        AssertShown("Claude now has the role of reviewer: You review what others propose or change.", _harness.Output);
        AssertShown("A role is what an agent is told to be; it does not limit what the agent can do.", _harness.Output);
        AssertShown("Claude no longer has a role, and will be told so with the next message.", _harness.Output);
    }

    [Fact]
    public async Task Role_command_lists_roles_and_accepts_one_of_the_users_own()
    {
        _harness.InstallAgent("claude", ClaudeReply("unused"));

        var output = await _harness.ChatAsync(
            ["start"],
            "/role",
            "/role codex Tester: You write a failing test before any fix",
            "/role claude reviewer: Only comment on naming.",
            "/role",
            "/help",
            "/exit");

        AssertShown("Claude no role", output);
        AssertShown("Roles: implementer, reviewer, architect, coordinator.", output);
        AssertShown("Codex now has the role of tester: You write a failing test before any fix. A role is what an agent is told to be", output);
        AssertShown("Claude now has the role of reviewer: Only comment on naming. This replaces the built-in description of reviewer for Claude only.", output);
        AssertShown("Codex tester: You write a failing test before any fix", output);
        AssertShown("Claude reviewer: Only comment on naming.", output);
        AssertShown("/role <agent> <role> gives one", output);
        AssertNotShown("Recorded.", output);
    }

    [Fact]
    public async Task Role_command_that_cannot_be_carried_out_says_why_and_changes_nothing()
    {
        InstallBothAgents();
        var tooLong = new string('x', 501);

        var output = await _harness.ChatAsync(
            ["start"],
            "/role claude architect",
            "/role gemini reviewer",
            "/role claude",
            "/role claude tester",
            "/role claude qa lead: tests things",
            "/role claude Codex: reviews things",
            "/role codex implementer",
            "/role codex none",
            "/role claude tester:",
            $"/role claude tester: {tooLong}",
            "/role codex none",
            "/role",
            "@claude hi",
            "/exit");

        AssertShown("No agent named gemini in this room. Agents here: claude, codex.", output);
        AssertShown("Which role? /role claude <role> with one of implementer, reviewer, architect, coordinator", output);
        AssertShown("No role named tester. Use one of implementer, reviewer, architect, coordinator, or give your own a description: /role claude <name>: <description>. Claude's role is unchanged.", output);
        AssertShown("A role name is letters, digits and hyphens only. Claude's role is unchanged.", output);
        AssertShown("codex is the name of an agent in this room, so it cannot be the name of a role. Claude's role is unchanged.", output);
        AssertShown("Codex no longer has a role.", output);
        AssertNotShown("Codex no longer has a role, and will be told so", output);
        AssertShown("A role needs a description after the colon. Claude's role is unchanged.", output);
        AssertShown("A role description is at most 500 characters; this one is 501. Claude's role is unchanged.", output);
        AssertShown("Codex has no role.", output);
        AssertShown("Claude architect: You decide structure before code is written", output);
        Assert.Contains("the role of architect in this room", _harness.StandardInput("claude"));
    }

    [Fact]
    public async Task Line_that_starts_with_a_command_word_is_never_recorded_as_a_message()
    {
        InstallBothAgents();

        var output = await _harness.ChatAsync(["start"], "/frobnicate now", "/help me", "/exit now", "/usr is full", "/etc/hosts looks wrong", "/history", "/exit");

        AssertShown("Unknown command /frobnicate now. Type /help. A line that starts with /word is never sent as a message.", output);
        AssertShown("Unknown command /help me.", output);
        AssertShown("Unknown command /exit now.", output);
        AssertShown("Unknown command /usr is full.", output);
        AssertShown("You: /etc/hosts looks wrong", output);
        Assert.Equal(1, output.Split("Recorded.").Length - 1);
        AssertNotShown("You: /frobnicate", output);
        AssertNotShown("You: /usr", output);
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
        AssertShown("/restart claude starts a new conversation with it and shows it the room again", output);
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
    public async Task Codex_outside_a_git_repository_says_what_to_do_and_is_usable_again_afterwards()
    {
        const string refusal = "Not inside a trusted directory and --skip-git-repo-check was not specified.\n";
        _harness.InstallAgent("codex", stdout: "", stderr: refusal, exitCode: 1);
        var relay = _harness.Start("start");
        await relay.StandardInput.WriteLineAsync("@codex hi");
        await Poll.UntilAsync(() => Squash(_harness.Output).Contains(Squash("only runs inside a Git repository")), "the explanation is shown");

        _harness.SetAgentOutput("codex", CodexReply("pong"));
        await relay.StandardInput.WriteLineAsync("@codex hi again");
        await relay.StandardInput.WriteLineAsync("/exit");
        await relay.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(0, relay.ExitCode);
        AssertShown("Start Relay from a repository, or run 'git init' here", _harness.Output);
        AssertShown("Relay does not pass --skip-git-repo-check for you", _harness.Output);
        AssertNotShown("Not inside a trusted directory", _harness.Output);
        AssertShown("Codex: pong", _harness.Output);
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
