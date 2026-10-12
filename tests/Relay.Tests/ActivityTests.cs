using Relay.Agents.Claude;
using Relay.Agents.Codex;
using Relay.Agents.Processes;
using Relay.Core.Agents;
using Relay.Core.Rooms;
using Relay.Core.Routing;

namespace Relay.Tests;

/// <summary>What an agent does during a turn: read from the CLI's output as it is written, and shown to the user only.</summary>
public sealed class ActivityTests : IDisposable
{
    // Shaped like what claude 2.1.273 prints with --output-format stream-json --verbose: Claude reads
    // a file, is refused a write, runs a command, then replies.
    private const string ClaudeStream = """
        {"type":"system","subtype":"init","cwd":"/work","session_id":"ce6f4686-9f97-4a98-8fb4-3125a2b0e671","tools":["Read","Write"]}
        {"type":"rate_limit_event","rate_limit_info":{"status":"allowed"}}
        {"type":"assistant","message":{"content":[{"type":"thinking","thinking":"","signature":"x"}]}}
        {"type":"assistant","message":{"content":[{"type":"text","text":"Reading hello.txt."}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"/work/hello.txt"}}]}}
        {"type":"user","message":{"content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"hello relay"}]}}
        {"type":"assistant","message":{"content":[{"type":"text","text":"Writing out.txt."},{"type":"tool_use","id":"toolu_2","name":"Write","input":{"file_path":"/work/out.txt","content":"HELLO RELAY"}}]}}
        {"type":"system","subtype":"permission_denied","tool_name":"Write","tool_use_id":"toolu_2","message":"Claude requested permissions to write to /work/out.txt, but you haven't granted it yet."}
        {"type":"user","message":{"content":[{"type":"tool_result","content":"denied","is_error":true,"tool_use_id":"toolu_2"}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_3","name":"PowerShell","input":{"command":"git status --short","description":"Show short git status"}}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_4","name":"TodoWrite","input":{"todos":[]}}]}}
        {"type":"assistant","message":{"content":[{"type":"text","text":"Read it, but could not write out.txt."}]}}
        {"type":"result","subtype":"success","is_error":false,"result":"Read it, but could not write out.txt.","session_id":"ce6f4686-9f97-4a98-8fb4-3125a2b0e671","permission_denials":[{"tool_name":"Write"}]}
        """;

    // Shaped like what codex-cli 0.162.0 prints with exec --json, on Windows and then on Unix.
    private const string CodexStream = """
        {"type":"thread.started","thread_id":"01a12603-b1c3-7d00-bf0c-d5720f7caecd"}
        {"type":"turn.started"}
        {"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"I'll check the status.\n"}}
        {"type":"item.started","item":{"id":"item_1","type":"command_execution","command":"\"C:\\\\Program Files\\\\PowerShell\\\\7\\\\pwsh.exe\" -Command 'git status --short'","aggregated_output":"","exit_code":null,"status":"in_progress"}}
        {"type":"item.completed","item":{"id":"item_1","type":"command_execution","command":"\"C:\\\\Program Files\\\\PowerShell\\\\7\\\\pwsh.exe\" -Command 'git status --short'","aggregated_output":"?? a\n","exit_code":0,"status":"completed"}}
        {"type":"item.started","item":{"id":"item_2","type":"command_execution","command":"/bin/bash -lc \"git no-such-subcommand\"","aggregated_output":"","exit_code":null,"status":"in_progress"}}
        {"type":"item.completed","item":{"id":"item_2","type":"command_execution","command":"/bin/bash -lc \"git no-such-subcommand\"","aggregated_output":"not a git command","exit_code":1,"status":"failed"}}
        {"type":"item.started","item":{"id":"item_3","type":"command_execution","command":"dotnet build","aggregated_output":"","exit_code":null,"status":"in_progress"}}
        {"type":"item.completed","item":{"id":"item_4","type":"agent_message","text":"One command failed."}}
        {"type":"turn.completed","usage":{"input_tokens":1}}
        """;

    private readonly RelayHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static AgentActivity Note(string text) => new(AgentActivityKind.Note, text);

    private static AgentActivity Action(string text) => new(AgentActivityKind.Action, text);

    private static AgentActivity Problem(string text) => new(AgentActivityKind.Problem, text);

    private static string[] Lines(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public void Claude_parser_reads_the_reply_and_session_id_from_the_end_of_a_stream()
        => Assert.Equal(
            new("Read it, but could not write out.txt.", "ce6f4686-9f97-4a98-8fb4-3125a2b0e671"),
            ClaudeOutputParser.Parse(new(0, ClaudeStream, string.Empty)));

    [Fact]
    public void Claude_parser_does_not_take_a_stream_that_never_reached_a_result_for_a_reply()
    {
        var cutShort = string.Join('\n', Lines(ClaudeStream)[..^1]);

        var ex = Assert.Throws<AgentException>(() => ClaudeOutputParser.Parse(new(1, cutShort, "killed")));

        Assert.Equal("claude exited with code 1: killed", ex.Message);
    }

    [Fact]
    public void Claude_parser_is_not_misled_by_a_line_after_the_result_that_is_not_part_of_the_stream()
        => Assert.Equal(
            new("Read it, but could not write out.txt.", "ce6f4686-9f97-4a98-8fb4-3125a2b0e671"),
            ClaudeOutputParser.Parse(new(0, ClaudeStream + "\n{\"warning\":\"a newer version is available\"}\n", string.Empty)));

    [Fact]
    public void Claude_parser_reads_a_result_printed_on_its_own_over_several_lines()
        => Assert.Equal(
            new("pong", "ce6f4686-9f97-4a98-8fb4-3125a2b0e671"),
            ClaudeOutputParser.Parse(new(0, """
                {
                  "is_error": false,
                  "result": "pong",
                  "session_id": "ce6f4686-9f97-4a98-8fb4-3125a2b0e671"
                }
                """, string.Empty)));

    [Fact]
    public void Claude_activity_is_what_it_said_each_tool_it_used_and_each_tool_it_was_refused()
        => Assert.Equal(
            [
                Note("Reading hello.txt."),
                Action("Read: /work/hello.txt"),
                Note("Writing out.txt."),
                Action("Write: /work/out.txt"),
                Problem("Write was not allowed: Claude requested permissions to write to /work/out.txt, but you haven't granted it yet."),
                Action("PowerShell: git status --short"),
                Action("TodoWrite"),
                Note("Read it, but could not write out.txt."),
            ],
            Lines(ClaudeStream).SelectMany(ClaudeOutputParser.ParseActivity));

    [Fact]
    public void Codex_activity_is_what_it_said_each_command_it_started_and_each_that_failed()
        => Assert.Equal(
            [
                Note("I'll check the status."),
                Action("Running: git status --short"),
                Action("Running: git no-such-subcommand"),
                Problem("Command failed (exit code 1): git no-such-subcommand"),
                Action("Running: dotnet build"),
                Note("One command failed."),
            ],
            Lines(CodexStream).SelectMany(CodexOutputParser.ParseActivity));

    [Theory]
    [InlineData(@"bash -lc ""echo \""hi\""""", @"echo ""hi""")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -Command 'dotnet test'", "dotnet test")]
    // Not a shell, so the program is part of what ran.
    [InlineData(@"python -c ""import shutil; shutil.rmtree('build')""", @"python -c ""import shutil; shutil.rmtree('build')""")]
    [InlineData(@"gcc -c ""a.c""", @"gcc -c ""a.c""")]
    // The quoted part is not the whole of what the shell was given.
    [InlineData("bash -lc 'echo a' && rm -rf 'x'", "bash -lc 'echo a' && rm -rf 'x'")]
    [InlineData(@"bash -lc ""echo a"" && rm -rf ""x""", @"bash -lc ""echo a"" && rm -rf ""x""")]
    public void Codex_command_is_shown_without_its_shell_only_when_the_shell_is_all_that_is_taken_away(string command, string shown)
        => Assert.Equal(
            [Action($"Running: {shown}")],
            CodexOutputParser.ParseActivity($$$"""{"type":"item.started","item":{"type":"command_execution","command":{{{System.Text.Json.JsonSerializer.Serialize(command)}}}}}"""));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("{}")]
    [InlineData("""{"type":"assistant"}""")]
    [InlineData("""{"type":"assistant","message":"text"}""")]
    [InlineData("""{"type":"assistant","message":{"content":"text"}}""")]
    [InlineData("""{"type":"assistant","message":{"content":[7,{"type":"text","text":"  "},{"type":"text"}]}}""")]
    [InlineData("""{"type":"item.started","item":7}""")]
    [InlineData("""{"type":"item.started","item":{"type":"command_execution"}}""")]
    [InlineData("""{"type":"item.completed","item":{"type":"agent_message","text":" "}}""")]
    public void Output_that_is_not_an_activity_is_ignored_by_both_parsers(string line)
    {
        Assert.Empty(ClaudeOutputParser.ParseActivity(line));
        Assert.Empty(CodexOutputParser.ParseActivity(line));
    }

    [Fact]
    public async Task Session_reports_activity_but_not_the_note_that_is_the_reply()
    {
        var provider = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), new FakeProcessRunner(_ => new(0, ClaudeStream, string.Empty)));
        await using var session = provider.CreateSession(await provider.DiscoverAsync(), new("/work"));
        var reported = new List<AgentActivity>();

        var reply = await session.SendAsync("hi", reported.Add);

        Assert.Equal("Read it, but could not write out.txt.", reply);
        Assert.Equal(7, reported.Count);
        Assert.Equal(Note("Reading hello.txt."), reported[0]);
        Assert.Equal(Action("TodoWrite"), reported[^1]);
    }

    [Fact]
    public async Task Session_reports_the_last_note_of_a_turn_that_failed_because_it_was_not_a_reply()
    {
        // The turn ran out of turns after Claude said what it was about to do: no reply follows.
        var cutShort = string.Join('\n', Lines(ClaudeStream)[..4]) + "\n" + """{"type":"result","subtype":"error_max_turns","is_error":true}""";
        var provider = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), new FakeProcessRunner(_ => new(0, cutShort, string.Empty)));
        await using var session = provider.CreateSession(await provider.DiscoverAsync(), new("/work"));
        var reported = new List<AgentActivity>();

        await Assert.ThrowsAsync<AgentException>(() => session.SendAsync("hi", reported.Add));

        Assert.Equal([Note("Reading hello.txt.")], reported);
    }

    [Fact]
    public async Task Session_does_not_report_the_last_note_of_a_failed_turn_when_the_failure_says_the_same()
    {
        var stream = """
            {"type":"assistant","message":{"content":[{"type":"text","text":"Not logged in"}]}}
            {"type":"result","is_error":true,"result":"Not logged in"}
            """;
        var provider = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), new FakeProcessRunner(_ => new(1, stream, string.Empty)));
        await using var session = provider.CreateSession(await provider.DiscoverAsync(), new("/work"));
        var reported = new List<AgentActivity>();

        var ex = await Assert.ThrowsAsync<AgentException>(() => session.SendAsync("hi", reported.Add));

        Assert.Equal("Not logged in", ex.Message);
        Assert.Empty(reported);
    }

    [Fact]
    public async Task Session_does_not_read_the_output_line_by_line_when_nobody_asked_for_activity()
    {
        var runner = new FakeProcessRunner(_ => new(0, ClaudeStream, string.Empty));
        var provider = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), runner);
        await using var session = provider.CreateSession(await provider.DiscoverAsync(), new("/work"));

        await session.SendAsync("hi");

        Assert.Null(runner.Requests[^1].OnOutputLine);
    }

    [Fact]
    public async Task Router_reports_activity_with_its_agent_and_keeps_it_out_of_the_room_and_away_from_other_agents()
    {
        var room = new InMemoryRoomManager().CreateRoom("general");
        var claude = new FakeSession("done");
        claude.Activity.Add(Action("Read: SECRET-ACTIVITY.txt"));
        var codex = new FakeSession("hello");
        room.AddParticipant(new AgentParticipant("claude", "Claude", claude));
        room.AddParticipant(new AgentParticipant("codex", "Codex", codex));
        var seen = new List<(string Agent, AgentActivity Activity)>();
        var events = new ExchangeEvents { Activity = (agent, activity) => seen.Add((agent.Id, activity)) };
        var router = new MessageRouter();

        await router.SendHumanMessageAsync(room, "@claude hi", events);
        await router.SendHumanMessageAsync(room, "@codex hi", events);

        Assert.Equal([("claude", Action("Read: SECRET-ACTIVITY.txt"))], seen);
        Assert.DoesNotContain(room.Messages, message => message.Content.Contains("SECRET-ACTIVITY"));
        Assert.DoesNotContain("SECRET-ACTIVITY", Assert.Single(codex.Received));
    }

    [Fact]
    public async Task Runner_hands_over_each_line_of_output_while_the_process_is_still_running()
    {
        var request = OperatingSystem.IsWindows()
            ? new ProcessRequest("powershell", ["-NoProfile", "-NonInteractive", "-Command", "Write-Output first; Start-Sleep -Seconds 4; Write-Output second"])
            : new ProcessRequest("sh", ["-c", "echo first; sleep 4; echo second"]);
        var lines = new List<string>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = new ProcessRunner().RunAsync(request with
        {
            OnOutputLine = line =>
            {
                lock (lines)
                    lines.Add(line);
                first.TrySetResult();
            },
        });
        await first.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(run.IsCompleted);
        var result = await run;
        Assert.Equal(["first", "second"], lines);
        Assert.Equal(["first", "second"], Lines(result.StandardOutput));
    }

    [Fact]
    public async Task Runner_reads_the_output_to_its_end_when_the_callback_throws_and_does_not_call_it_again()
    {
        // More output than a pipe holds, so a process whose output is no longer read never exits.
        var request = OperatingSystem.IsWindows()
            ? new ProcessRequest("powershell", ["-NoProfile", "-NonInteractive", "-Command", "1..20000 | ForEach-Object { 'line ' + $_ }"])
            : new ProcessRequest("sh", ["-c", "i=1; while [ $i -le 20000 ]; do echo \"line $i\"; i=$((i+1)); done"]);
        var calls = 0;

        var result = await new ProcessRunner().RunAsync(request with
        {
            Timeout = TimeSpan.FromSeconds(60),
            OnOutputLine = _ =>
            {
                calls++;
                throw new IOException("The console is gone.");
            },
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("line 20000", Lines(result.StandardOutput)[^1]);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Runner_does_not_hand_over_what_a_background_process_writes_after_the_run_has_ended()
    {
        // The background process keeps the output pipe open and writes its line only once the test
        // says so, by creating "go", and then says it has by creating "done". Nothing here depends
        // on how long the shell takes to start or to exit. It gives up by itself after 30 seconds,
        // so a failed test leaves nothing running.
        var request = OperatingSystem.IsWindows()
            ? new ProcessRequest("powershell", ["-NoProfile", "-NonInteractive", "-Command",
                "Write-Output first; Start-Process powershell -NoNewWindow -ArgumentList '-NoProfile', '-NonInteractive', '-Command', "
                + "'for ($i = 0; $i -lt 300 -and -not (Test-Path go); $i++) { Start-Sleep -Milliseconds 100 }; if (Test-Path go) { Write-Output late }; Set-Content done 1'"])
            : new ProcessRequest("sh", ["-c",
                "echo first; (i=0; while [ ! -e go ] && [ $i -lt 300 ]; do sleep 0.1; i=$((i+1)); done; [ -e go ] && echo late; : > done) &"]);
        var lines = new List<string>();

        ThreadPool.GetMinThreads(out var minimum, out _);
        var before = $"minimum {minimum}, threads {ThreadPool.ThreadCount}, pending {ThreadPool.PendingWorkItemCount}";
        Console.WriteLine($"#49 pool before: {before}");
        var ran = System.Diagnostics.Stopwatch.StartNew();
        var result = await new ProcessRunner().RunAsync(request with
        {
            WorkingDirectory = _harness.Directory,
            OnOutputLine = line =>
            {
                lock (lines)
                    lines.Add(line);
            },
        });
        ran.Stop();
        var after = $"threads {ThreadPool.ThreadCount}, pending {ThreadPool.PendingWorkItemCount}";
        var queued = System.Diagnostics.Stopwatch.StartNew();
        await Task.Run(() => { });
        // TEMPORARY, for #49: what the thread pool looked like when the first line went missing on CI.
        Assert.True(Lines(result.StandardOutput).SequenceEqual(["first"]),
            $"stdout '{result.StandardOutput}', stderr '{result.StandardError}', exit {result.ExitCode}, run took {ran.ElapsedMilliseconds} ms, "
            + $"cores {Environment.ProcessorCount}, pool before: {before}, after: {after}, a queued item waited {queued.ElapsedMilliseconds} ms");
        File.WriteAllText(_harness.PathTo("go"), string.Empty);
        await Poll.UntilAsync(() => File.Exists(_harness.PathTo("done")), "the background process has written its line");
        // The line is in the pipe by now. This is for the reader still attached to it to pass it on, if it were going to.
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Equal(["first"], Lines(result.StandardOutput));
        lock (lines)
            Assert.Equal(["first"], lines);
    }

    [Fact]
    public async Task Chatroom_shows_what_the_agent_did_before_its_reply_and_flags_what_it_was_refused()
    {
        _harness.InstallAgent("claude", ClaudeStream);

        var output = await _harness.ChatAsync(["start"], "@claude hi", "/exit");

        string[] expected =
        [
            "Waiting for Claude...",
            "- Reading hello.txt.",
            "- Read: /work/hello.txt",
            "- Writing out.txt.",
            "! Write was not allowed: Claude requested permissions to write to /work/out.txt, but you haven't granted it yet.",
            "- PowerShell: git status --short",
            "- TodoWrite",
            "Claude: Read it, but could not write out.txt.",
        ];
        var squashed = RelayHarness.Squash(output);
        var positions = expected.Select(line => squashed.IndexOf(RelayHarness.Squash(line), StringComparison.Ordinal)).ToArray();
        Assert.True(positions.All(p => p >= 0) && positions.SequenceEqual(positions.Order()), $"Expected these lines in order:\n{string.Join('\n', expected)}\n\nin:\n{output}");
        // The reply is shown once, as the reply, and not also as a note above it.
        Assert.Equal(squashed.IndexOf("Readit,but", StringComparison.Ordinal), squashed.LastIndexOf("Readit,but", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Activity_cannot_send_escape_sequences_add_lines_of_its_own_or_run_on_without_end()
    {
        // A note with a clear-screen sequence, a line break followed by a forged prompt, and console
        // markup; then a command far longer than anyone would read.
        _harness.InstallAgent("claude", $$$"""
            {"type":"assistant","message":{"content":[{"type":"text","text":"safe\u001b[2J\u202Enote\nYou: [red]forged[/]"}]}}
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"{{{new string('x', 500)}}}"}}]}}
            {{{RelayHarness.ClaudeReply("pong")}}}
            """);

        var output = await _harness.ChatAsync(["start"], "@claude hi", "/exit");

        RelayHarness.AssertShown("- safe[2Jnote You: [red]forged[/]", output);
        Assert.DoesNotContain('\u001b', output);
        Assert.DoesNotContain('\u202E', output);
        Assert.DoesNotContain(output.Split('\n'), line => line.TrimStart().StartsWith("You: [red]", StringComparison.Ordinal));
        RelayHarness.AssertShown($"- Bash: {new string('x', 194)}...", output);
        RelayHarness.AssertNotShown(new string('x', 195), output);
        RelayHarness.AssertShown("Claude: pong", output);
    }

    [Fact]
    public async Task Activity_shows_a_path_in_the_working_folder_relative_to_it()
    {
        var file = Path.Combine(_harness.Directory, "src", "Thing.cs").Replace(@"\", @"\\");
        _harness.InstallAgent("claude", $$$"""
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"{{{file}}}"}}]}}
            {{{RelayHarness.ClaudeReply("pong")}}}
            """);

        var output = await _harness.ChatAsync(["start"], "@claude hi", "/exit");

        // macOS reports the temp folder through a /private symlink, so there the path is not under
        // the folder Relay sees itself in, and is shown in full.
        RelayHarness.AssertShown(OperatingSystem.IsMacOS() ? "Thing.cs" : $"- Read: {Path.Combine("src", "Thing.cs")}", output);
    }

    [Fact]
    public async Task Activity_shows_a_path_that_only_contains_the_working_folder_in_full()
    {
        // The working folder's path appears inside this one, but the file is somewhere else.
        var file = ("backup" + Path.Combine(_harness.Directory, "Thing.cs")).Replace(@"\", @"\\");
        _harness.InstallAgent("claude", $$$"""
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"{{{file}}}"}}]}}
            {{{RelayHarness.ClaudeReply("pong")}}}
            """);

        var output = await _harness.ChatAsync(["start"], "@claude hi", "/exit");

        RelayHarness.AssertNotShown("- Read: backupThing.cs", output);
        RelayHarness.AssertShown($"{Path.GetFileName(_harness.Directory)}{Path.DirectorySeparatorChar}Thing.cs", output);
    }

    [Fact]
    public async Task Activity_cut_short_does_not_end_in_half_a_character()
    {
        // U+1F600 is two chars in .NET, and they fall either side of where the line is cut.
        _harness.InstallAgent("claude", $$$"""
            {"type":"assistant","message":{"content":[{"type":"text","text":"{{{new string('x', 199)}}}😀 and more"}]}}
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"a.txt"}}]}}
            {{{RelayHarness.ClaudeReply("pong")}}}
            """);

        var output = await _harness.ChatAsync(["start"], "@claude hi", "/exit");

        RelayHarness.AssertShown($"- {new string('x', 199)}...", output);
        Assert.DoesNotContain('\ud83d', output);
        Assert.DoesNotContain('�', output);
    }

    [Fact]
    public async Task Codex_activity_is_shown_in_the_chatroom_too()
    {
        _harness.InstallAgent("codex", CodexStream);

        var output = await _harness.ChatAsync(["start"], "@codex hi", "/exit");

        RelayHarness.AssertShown("- I'll check the status.", output);
        RelayHarness.AssertShown("- Running: git status --short", output);
        RelayHarness.AssertShown("! Command failed (exit code 1): git no-such-subcommand", output);
        RelayHarness.AssertShown("Codex: One command failed.", output);
    }
}
