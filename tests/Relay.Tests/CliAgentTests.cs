using Relay.Agents.Cli;
using Relay.Agents.Claude;
using Relay.Agents.Codex;
using Relay.Agents.Processes;
using Relay.Core.Agents;

namespace Relay.Tests;

public class CliAgentTests
{
    private const string ClaudeOk = """{"type":"result","is_error":false,"result":"pong","session_id":"ce6f4686-9f97-4a98-8fb4-3125a2b0e671"}""";

    private const string CodexOk = """
        {"type":"thread.started","thread_id":"01a12603-b1c3-7d00-bf0c-d5720f7caecd"}
        {"type":"turn.started"}
        {"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"working on it"}}
        {"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"pong"}}
        {"type":"turn.completed","usage":{"input_tokens":1}}
        """;

    private static ProcessResult Ok(string stdout) => new(0, stdout, string.Empty);

    [Fact]
    public async Task Discovery_reports_path_and_version_when_the_cli_is_on_path()
    {
        var runner = new FakeProcessRunner(_ => Ok("2.1.273 (Claude Code)\n"));
        var provider = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), runner);

        var result = await provider.DiscoverAsync(includeVersion: true);

        Assert.True(result.IsInstalled);
        Assert.Equal("/usr/bin/claude", result.ExecutablePath);
        Assert.Equal("2.1.273 (Claude Code)", result.Version);
        Assert.Equal(["--version"], Assert.Single(runner.Requests).Arguments);
    }

    [Fact]
    public async Task Discovery_still_reports_installed_when_the_version_cannot_be_read()
    {
        var provider = new CodexProvider(new FakeLocator("/usr/bin/codex"), new FakeProcessRunner(_ => throw new AgentException("no")));

        var result = await provider.DiscoverAsync(includeVersion: true);

        Assert.True(result.IsInstalled);
        Assert.Null(result.Version);
    }

    [Fact]
    public async Task Discovery_does_not_launch_the_cli_unless_the_version_is_requested()
    {
        var runner = new FakeProcessRunner(_ => Ok("1.0"));

        var result = await new ClaudeProvider(new FakeLocator("/usr/bin/claude"), runner).DiscoverAsync();

        Assert.True(result.IsInstalled);
        Assert.Null(result.Version);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Discovery_reports_a_missing_cli_without_launching_anything()
    {
        var runner = new FakeProcessRunner(_ => Ok(string.Empty));
        var provider = new CodexProvider(new FakeLocator(null), runner);

        var result = await provider.DiscoverAsync();

        Assert.False(result.IsInstalled);
        Assert.Null(result.ExecutablePath);
        Assert.Contains("not found on PATH", result.Detail);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Claude_session_sends_the_message_on_stdin_and_resumes_the_same_conversation()
    {
        var runner = new FakeProcessRunner(_ => Ok(ClaudeOk));
        var provider = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), runner);
        await using var session = provider.CreateSession(await provider.DiscoverAsync(), new("/work"));
        runner.Requests.Clear();

        Assert.Equal("pong", await session.SendAsync("first; rm -rf /"));
        await session.SendAsync("second");

        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose", "--setting-sources", "user"], runner.Requests[0].Arguments);
        Assert.Equal("first; rm -rf /", runner.Requests[0].StandardInput);
        Assert.Equal("/work", runner.Requests[0].WorkingDirectory);
        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose", "--setting-sources", "user", "--resume", "ce6f4686-9f97-4a98-8fb4-3125a2b0e671"], runner.Requests[1].Arguments);
        Assert.Equal(AgentSessionState.Ready, session.State);
    }

    [Fact]
    public void Claude_loads_project_settings_only_for_a_trusted_workspace()
    {
        var provider = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), new FakeProcessRunner(_ => Ok(ClaudeOk)));

        var untrusted = provider.BuildArguments(sessionId: null, new("/work"));
        var trusted = provider.BuildArguments(sessionId: null, new("/work", TrustWorkspace: true));

        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose", "--setting-sources", "user"], untrusted);
        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose"], trusted);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "ce6f4686-9f97-4a98-8fb4-3125a2b0e671")]
    [InlineData(true, "ce6f4686-9f97-4a98-8fb4-3125a2b0e671")]
    public void Providers_never_pass_flags_that_approve_tool_use_or_weaken_sandboxing(bool trustWorkspace, string? sessionId)
    {
        var runner = new FakeProcessRunner(_ => Ok(ClaudeOk));
        var locator = new FakeLocator("/usr/bin/agent");
        var options = new AgentSessionOptions("/work", trustWorkspace);
        string[] forbidden = ["danger", "bypass", "skip", "permission", "approve", "approval", "sandbox", "full-auto", "allowed", "yolo"];

        foreach (var provider in new CliAgentProvider[] { new ClaudeProvider(locator, runner), new CodexProvider(locator, runner) })
        {
            foreach (var argument in provider.BuildArguments(sessionId, options))
                Assert.DoesNotContain(forbidden, word => argument.Contains(word, StringComparison.OrdinalIgnoreCase));

            // With somebody to ask, the only additions are the two that say where to ask. Naming a tool
            // that asks the user approves nothing, and nothing else that mentions permissions comes with it.
            var tool = new ApprovalTool("/opt/relay/relay", ["approval-tool", "pipe"], "/tmp/relay-approval-test");
            var asking = provider.BuildArguments(sessionId, options with { ApprovalTool = tool }).ToList();
            if (asking.IndexOf("--permission-prompt-tool") is var at and >= 0)
            {
                Assert.Equal(ClaudeProvider.PermissionPromptTool, asking[at + 1]);
                asking.RemoveRange(at, 2);
                asking.RemoveRange(asking.IndexOf("--mcp-config"), 2);
            }

            Assert.Equal(provider.BuildArguments(sessionId, options), asking);
        }
    }

    [Fact]
    public async Task Codex_session_resumes_the_thread_it_started()
    {
        var runner = new FakeProcessRunner(_ => Ok(CodexOk));
        var provider = new CodexProvider(new FakeLocator("/usr/bin/codex"), runner);
        await using var session = provider.CreateSession(await provider.DiscoverAsync(), new("/work"));
        runner.Requests.Clear();

        Assert.Equal("pong", await session.SendAsync("first"));
        await session.SendAsync("second");

        Assert.Equal(["exec", "--json", "-"], runner.Requests[0].Arguments);
        Assert.Equal(["exec", "resume", "--json", "01a12603-b1c3-7d00-bf0c-d5720f7caecd", "-"], runner.Requests[1].Arguments);
    }

    [Theory]
    [InlineData("x & calc.exe")]
    [InlineData("--dangerously-skip-permissions")]
    [InlineData("-c")]
    public async Task Session_never_puts_an_unsafe_session_id_on_the_command_line(string sessionId)
    {
        var runner = new FakeProcessRunner(_ => Ok($$"""{"is_error":false,"result":"pong","session_id":"{{sessionId}}"}"""));
        var provider = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), runner);
        await using var session = provider.CreateSession(await provider.DiscoverAsync(), new("/work"));

        await session.SendAsync("first");
        await session.SendAsync("second");

        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose", "--setting-sources", "user"], runner.Requests[^1].Arguments);
    }

    [Fact]
    public async Task Session_is_faulted_after_a_failure_and_recovers_on_the_next_success()
    {
        var fail = true;
        var runner = new FakeProcessRunner(_ => fail ? new(1, string.Empty, "boom") : Ok(ClaudeOk));
        var provider = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), runner);
        await using var session = provider.CreateSession(new("claude", "Claude", true, "/usr/bin/claude"), new("/work"));

        await Assert.ThrowsAsync<AgentException>(() => session.SendAsync("hi"));
        Assert.Equal(AgentSessionState.Faulted, session.State);

        fail = false;
        await session.SendAsync("hi");
        Assert.Equal(AgentSessionState.Ready, session.State);
    }

    [Fact]
    public async Task Session_keeps_its_conversation_after_a_failure_until_it_is_reset()
    {
        var fail = false;
        var runner = new FakeProcessRunner(_ => fail ? new(1, string.Empty, "rate limited") : Ok(ClaudeOk));
        var provider = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), runner);
        await using var session = provider.CreateSession(new("claude", "Claude", true, "/usr/bin/claude"), new("/work"));
        await session.SendAsync("first");

        fail = true;
        await Assert.ThrowsAsync<AgentException>(() => session.SendAsync("second"));
        fail = false;
        await session.SendAsync("third");
        session.Reset();
        await session.SendAsync("fourth");

        Assert.Contains("--resume", runner.Requests[2].Arguments);
        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose", "--setting-sources", "user"], runner.Requests[3].Arguments);
    }

    [Fact]
    public async Task Reset_clears_a_faulted_state()
    {
        var provider = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), new FakeProcessRunner(_ => new(1, string.Empty, "boom")));
        await using var session = provider.CreateSession(new("claude", "Claude", true, "/usr/bin/claude"), new("/work"));
        await Assert.ThrowsAsync<AgentException>(() => session.SendAsync("hi"));

        session.Reset();

        Assert.Equal(AgentSessionState.Ready, session.State);
    }

    [Fact]
    public async Task Session_refuses_a_second_message_while_one_is_in_flight_and_is_unharmed_by_it()
    {
        var runner = new BlockingProcessRunner();
        await using var session = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), runner)
            .CreateSession(new("claude", "Claude", true, "/usr/bin/claude"), new("/work"));

        var first = session.SendAsync("one");
        await runner.Started;
        var ex = await Assert.ThrowsAsync<AgentException>(() => session.SendAsync("two"));
        Assert.Contains("still working", ex.Message);
        Assert.Equal(AgentSessionState.Busy, session.State);

        runner.Complete(Ok(ClaudeOk));
        Assert.Equal("pong", await first);
        Assert.Equal(AgentSessionState.Ready, session.State);
    }

    [Fact]
    public async Task Turn_cancelled_part_way_through_leaves_the_session_ready_and_its_conversation_intact()
    {
        var runner = new BlockingProcessRunner();
        await using var session = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), runner)
            .CreateSession(new("claude", "Claude", true, "/usr/bin/claude"), new("/work"));
        var first = session.SendAsync("one");
        await runner.Started;
        runner.Complete(Ok(ClaudeOk));
        await first;

        using var cts = new CancellationTokenSource();
        var second = session.SendAsync("two", cancellationToken: cts.Token);
        await runner.Started;
        Assert.Equal(AgentSessionState.Busy, session.State);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(AgentSessionState.Ready, session.State);

        var third = session.SendAsync("three");
        await runner.Started;
        runner.Complete(Ok(ClaudeOk));
        await third;
        Assert.Contains("--resume", runner.Requests[^1].Arguments);
    }

    [Fact]
    public async Task Turn_cancelled_before_it_starts_leaves_the_session_ready()
    {
        var runner = new FakeProcessRunner(_ => Ok(ClaudeOk));
        await using var session = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), runner)
            .CreateSession(new("claude", "Claude", true, "/usr/bin/claude"), new("/work"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendAsync("one", cancellationToken: new CancellationToken(canceled: true)));

        Assert.Equal(AgentSessionState.Ready, session.State);
    }

    [Fact]
    public async Task Disposing_a_session_cancels_the_message_in_flight()
    {
        var runner = new BlockingProcessRunner();
        var session = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), runner)
            .CreateSession(new("claude", "Claude", true, "/usr/bin/claude"), new("/work"));
        var inFlight = session.SendAsync("one");
        await runner.Started;

        await session.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight);
        Assert.Equal(AgentSessionState.Closed, session.State);
    }

    [Fact]
    public async Task Session_rejects_use_after_dispose()
    {
        var provider = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), new FakeProcessRunner(_ => Ok(ClaudeOk)));
        var session = provider.CreateSession(new("claude", "Claude", true, "/usr/bin/claude"), new("/work"));

        await session.DisposeAsync();

        Assert.Equal(AgentSessionState.Closed, session.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SendAsync("hi"));
    }

    [Fact]
    public void Claude_parser_reads_the_result_and_session_id()
        => Assert.Equal(new("pong", "ce6f4686-9f97-4a98-8fb4-3125a2b0e671"), ClaudeOutputParser.Parse(Ok(ClaudeOk)));

    [Fact]
    public void Claude_parser_surfaces_the_cli_error_message()
    {
        var result = new ProcessResult(1, """{"is_error":true,"result":"Not logged in · Please run /login"}""", string.Empty);

        var ex = Assert.Throws<AgentException>(() => ClaudeOutputParser.Parse(result));

        Assert.Equal("Not logged in · Please run /login", ex.Message);
    }

    [Theory]
    [InlineData("", "fatal: something broke", "claude exited with code 2: fatal: something broke")]
    [InlineData("not json", "", "claude exited with code 2 and no output.")]
    public void Claude_parser_describes_non_json_output_from_the_exit_code_and_stderr(string stdout, string stderr, string expected)
    {
        var ex = Assert.Throws<AgentException>(() => ClaudeOutputParser.Parse(new(2, stdout, stderr)));

        Assert.Equal(expected, ex.Message);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{"foo":1}""")]
    [InlineData("""{"is_error":false,"result":42}""")]
    [InlineData("")]
    public void Claude_parser_says_so_when_a_successful_run_prints_something_it_cannot_read(string stdout)
    {
        var ex = Assert.Throws<AgentException>(() => ClaudeOutputParser.Parse(Ok(stdout)));

        Assert.StartsWith("claude finished without an error, but Relay could not read its output.", ex.Message);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("""{"no":"type"}""")]
    [InlineData("")]
    public void Codex_parser_says_so_when_a_successful_run_prints_something_it_cannot_read(string stdout)
    {
        var ex = Assert.Throws<AgentException>(() => CodexOutputParser.Parse(Ok(stdout)));

        Assert.StartsWith("codex finished without an error, but Relay could not read its output.", ex.Message);
    }

    [Fact]
    public void Codex_parser_reports_an_empty_answer_when_the_turn_ran_but_said_nothing()
    {
        var ex = Assert.Throws<AgentException>(() => CodexOutputParser.Parse(Ok("""{"type":"turn.completed"}""")));

        Assert.Equal("codex returned an empty response.", ex.Message);
    }

    [Fact]
    public void Failure_summary_keeps_only_the_end_of_a_long_error_output()
    {
        var stderr = string.Join('\n', Enumerable.Range(1, 50).Select(i => $"line {i} " + new string('x', 200)));

        var ex = Assert.Throws<AgentException>(() => ClaudeOutputParser.Parse(new(1, string.Empty, stderr)));

        Assert.StartsWith("claude exited with code 1: ", ex.Message);
        Assert.DoesNotContain("line 1 ", ex.Message);
        Assert.InRange(ex.Message.Length, 1, 450);
    }

    [Theory]
    [InlineData("""{"is_error":true}""")]
    [InlineData("""{"is_error":true,"result":""}""")]
    public void Claude_parser_does_not_call_a_run_successful_when_claude_flagged_an_error_without_a_message(string stdout)
    {
        var ex = Assert.Throws<AgentException>(() => ClaudeOutputParser.Parse(Ok(stdout)));

        Assert.Equal("claude reported an error without a message.", ex.Message);
    }

    [Fact]
    public void Claude_parser_falls_back_to_the_exit_code_and_stderr_when_an_error_has_no_message()
    {
        var ex = Assert.Throws<AgentException>(() => ClaudeOutputParser.Parse(new(1, """{"is_error":true}""", "quota exceeded")));

        Assert.Equal("claude exited with code 1: quota exceeded", ex.Message);
    }

    [Fact]
    public void Claude_parser_rejects_an_empty_result()
        => Assert.Throws<AgentException>(() => ClaudeOutputParser.Parse(Ok("""{"is_error":false,"result":""}""")));

    [Fact]
    public void Codex_parser_returns_the_last_agent_message_and_thread_id()
        => Assert.Equal(new("pong", "01a12603-b1c3-7d00-bf0c-d5720f7caecd"), CodexOutputParser.Parse(Ok(CodexOk)));

    [Theory]
    [InlineData("""{"type":"turn.failed","error":{"message":"usage limit reached"}}""")]
    [InlineData("""{"type":"error","message":"usage limit reached"}""")]
    public void Codex_parser_surfaces_error_events(string line)
    {
        var ex = Assert.Throws<AgentException>(() => CodexOutputParser.Parse(new(1, line, string.Empty)));

        Assert.Equal("usage limit reached", ex.Message);
    }

    [Fact]
    public void Codex_parser_ignores_a_transient_error_in_a_turn_that_completes()
    {
        var stdout = CodexOk.Replace("""{"type":"turn.started"}""", """{"type":"error","message":"Reconnecting... 1/5"}""");

        Assert.Equal(new("pong", "01a12603-b1c3-7d00-bf0c-d5720f7caecd"), CodexOutputParser.Parse(Ok(stdout)));
    }

    [Fact]
    public void Codex_parser_reports_an_error_event_when_no_answer_follows()
    {
        var ex = Assert.Throws<AgentException>(() => CodexOutputParser.Parse(Ok("""{"type":"error","message":"stream disconnected"}""")));

        Assert.Equal("stream disconnected", ex.Message);
    }

    [Fact]
    public void Codex_parser_falls_back_to_stderr_when_there_are_no_events()
    {
        var ex = Assert.Throws<AgentException>(() => CodexOutputParser.Parse(new(1, "garbage\n", "Not inside a trusted directory")));

        Assert.Equal("codex exited with code 1: Not inside a trusted directory", ex.Message);
    }

    [Fact]
    public void Codex_parser_explains_the_refusal_to_run_outside_a_git_repository()
    {
        var stderr = "Not inside a trusted directory and --skip-git-repo-check was not specified.";

        var ex = Assert.Throws<AgentException>(() => CodexOutputParser.Parse(new(1, string.Empty, stderr)));

        Assert.StartsWith("codex only runs inside a Git repository, and this folder is not one.", ex.Message);
        Assert.Contains("git init", ex.Message);
        Assert.DoesNotContain("Not inside a trusted directory", ex.Message);
        // The refusal happens before Codex starts a turn, so it has not been shown the room.
        Assert.False(ex.PromptMayHaveBeenRead);
    }

    [Theory]
    // Mentions the flag, but is not the refusal: the original diagnosis is what helps here.
    [InlineData("", "warning: --skip-git-repo-check is disabled. Authentication failed.")]
    // Names the whole clause, but a turn had already started, so it is not the startup refusal.
    [InlineData("""{"type":"thread.started","thread_id":"t1"}""", "--skip-git-repo-check was not specified")]
    public void Codex_parser_keeps_the_original_diagnosis_for_other_errors_that_mention_the_flag(string stdout, string stderr)
    {
        var ex = Assert.Throws<AgentException>(() => CodexOutputParser.Parse(new(1, stdout, stderr)));

        Assert.StartsWith("codex exited with code 1: ", ex.Message);
        Assert.DoesNotContain("only runs inside a Git repository", ex.Message);
        Assert.True(ex.PromptMayHaveBeenRead);
    }
}
