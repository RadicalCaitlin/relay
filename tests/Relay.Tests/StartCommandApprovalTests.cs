using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Relay.Cli.Commands;
using Relay.Cli.Ui;
using Relay.Core.Agents;
using Relay.Core.Rooms;
using Relay.Core.Routing;
using Relay.Core.Workspaces;
using Spectre.Console.Testing;
using static Relay.Tests.RelayHarness;

namespace Relay.Tests;

/// <summary>
/// The chatroom itself, run in process with a keyboard the test types on. What becomes of a line
/// typed around a question, and of Relay's own folder on each way out, happens only in a terminal,
/// and every test that runs the built CLI redirects its streams.
/// </summary>
public sealed class StartCommandApprovalTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // Every test here types at a chatroom that is reading the keyboard. One that stops reading, or
    // waits for a line that will not come, fails on its own instead of holding up the suite.
    private const int Limit = 60_000;

    private readonly TestConsole _console = new TestConsole().Interactive();
    private readonly Keyboard _keyboard = new();
    private readonly InMemoryRoomManager _rooms = new();

    public void Dispose()
    {
        // The end of input, so a chatroom a failed test left running stops reading.
        _keyboard.Type(null);
        _keyboard.Type(null);
        _console.Dispose();
    }

    /// <summary>A terminal's keyboard: a read blocks until a line is typed, and once begun it takes that line whoever it was begun for.</summary>
    private sealed class Keyboard : IConsoleStreams
    {
        private readonly BlockingCollection<(string? Line, bool MoreFollows)> _typed = new();
        private int _reads;
        private volatile bool _more;

        public bool IsInputRedirected => false;

        public bool IsOutputRedirected => false;

        /// <summary>Whether the rest of a paste is still waiting, as a terminal reports keys not yet read.</summary>
        public bool InputWaiting => _more;

        /// <summary>How many reads have begun. While the latest waits and no agent is working, Relay writes nothing.</summary>
        public int Reads => Volatile.Read(ref _reads);

        public void DiscardPendingInput()
        {
        }

        public string? ReadLine()
        {
            Interlocked.Increment(ref _reads);
            var (line, more) = _typed.Take();
            _more = more;
            return line;
        }

        /// <summary>One line, typed on its own.</summary>
        public void Type(string? line) => _typed.Add((line, false));

        /// <summary>Lines that arrive together, as a paste does: every line but the last has more behind it.</summary>
        public void Paste(params string[] lines)
        {
            for (var i = 0; i < lines.Length; i++)
                _typed.Add((lines[i], i < lines.Length - 1));
        }
    }

    private sealed class AskingProvider(Func<AgentSessionOptions, IAgentSession> createSession) : IAgentProvider
    {
        public string Id => "claude";

        public string DisplayName => "Claude";

        public bool CanAskForApproval => true;

        public AgentSessionOptions? Options { get; private set; }

        /// <summary>The folder Relay made for whatever the agent's CLI needs in order to find the approval tool.</summary>
        public string Folder => Options!.ApprovalTool!.Directory;

        public Task<AgentDiscoveryResult> DiscoverAsync(bool includeVersion = false, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentDiscoveryResult(Id, DisplayName, IsInstalled: true, "/bin/claude"));

        public IAgentSession CreateSession(AgentDiscoveryResult discovery, AgentSessionOptions options)
        {
            Options = options;
            return createSession(options);
        }
    }

    private (StartCommand Command, Task<int> Running) Start(AskingProvider provider, string? turnTimeout = null, params IAgentProvider[] others)
    {
        var command = new StartCommand(_console, new AgentDiscoveryService([provider, .. others]), _rooms, new MessageRouter(),
            new WorkspaceTrustPrompt(_console, new WorkspaceTrustStore(null), _keyboard), _keyboard);
        var settings = new StartCommand.Settings { TrustWorkspace = true, TurnTimeout = turnTimeout };
        // The context is Spectre's own and Relay never reads it.
        return (command, Task.Run(() => command.ExecuteAsync(null!, settings, CancellationToken.None)));
    }

    private Task ReadsAsync(int count) => Poll.UntilAsync(() => _keyboard.Reads >= count, $"Relay has read the keyboard {count} times");

    private async Task LeaveAsync(Task<int> running)
    {
        _keyboard.Type("/exit");
        Assert.Equal(0, await running.WaitAsync(Patience));
    }

    [Fact(Timeout = Limit)]
    public async Task A_paste_of_several_lines_is_one_message_and_is_counted_before_it_is_sent()
    {
        string? shownWhenAsked = null;
        var provider = new AskingProvider(_ => new FakeSession((_, _) =>
        {
            shownWhenAsked = _console.Output;
            return Task.FromResult("thanks");
        }));
        var (_, running) = Start(provider);
        await ReadsAsync(1);

        _keyboard.Paste("@claude look at this", "    indented", "", "/approvals off");
        await ReadsAsync(5);

        var message = Assert.Single(Assert.Single(_rooms.Rooms).Messages, m => m.SenderType == SenderType.Human);
        Assert.Equal("@claude look at this\n    indented\n\n/approvals off", message.Content);
        // Said before the message went anywhere, not merely somewhere in what Relay printed.
        AssertShown("Sending 4 lines as one message.", shownWhenAsked!);
        await LeaveAsync(running);
    }

    [Fact(Timeout = Limit)]
    public async Task A_command_on_any_line_but_the_first_of_a_paste_does_nothing()
    {
        var (_, running) = Start(new AskingProvider(_ => new FakeSession("thanks")));
        await ReadsAsync(1);
        // Both settings start at the value the pasted lines would change, so running either shows.
        _keyboard.Type("/pause on");
        await ReadsAsync(2);

        _keyboard.Paste("@claude look at this", "/pause off", "/approvals off");
        await ReadsAsync(5);

        // Read what each query itself printed. The header says the same about both settings, so
        // searching the whole run would pass even if the pasted lines had taken effect.
        var mark = _console.Output.Length;
        _keyboard.Type("/approvals");
        await ReadsAsync(6);
        AssertShown("Approvals are on", _console.Output[mark..]);

        mark = _console.Output.Length;
        _keyboard.Type("/pause");
        await ReadsAsync(7);
        AssertShown("Pause mode is on.", _console.Output[mark..]);
        await LeaveAsync(running);
    }

    [Fact(Timeout = Limit)]
    public async Task A_command_on_the_first_line_of_a_paste_runs_with_only_that_line()
    {
        var (_, running) = Start(new AskingProvider(_ => new FakeSession("thanks")));
        await ReadsAsync(1);

        _keyboard.Paste("/role claude reviewer", "and delete the build folder");
        await ReadsAsync(3);

        AssertShown("Only the first line was read as a command. The other 1 line was not used.", _console.Output);
        AssertShown("Claude now has the role of reviewer", _console.Output);
        AssertNotShown("delete the build folder", _console.Output);
        await LeaveAsync(running);
    }

    [Fact(Timeout = Limit)]
    public async Task A_pasted_answer_to_a_question_is_refused_whole_and_a_typed_answer_still_works()
    {
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? decision = null;
        var provider = new AskingProvider(options => new FakeSession(async (_, _) =>
        {
            // What the approval tool does for the agent's CLI: ask, then wait for the answer.
            await using var pipe = new NamedPipeClientStream(".", options.ApprovalTool!.Arguments[^1], PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("""{"tool_name":"Bash","input":{"command":"rm -rf build"}}""");
            asked.SetResult();
            using var reader = new StreamReader(pipe, leaveOpen: true);
            return decision = await reader.ReadLineAsync() ?? "nothing";
        }));
        var (_, running) = Start(provider);
        await ReadsAsync(1);

        _keyboard.Type("@claude tidy up");
        await asked.Task.WaitAsync(Patience);
        await ReadsAsync(2);

        // Pasted onto the open question, with a yes on its first line.
        _keyboard.Paste("yes", "rm -rf /");
        await ReadsAsync(4);
        AssertShown("That arrived as 2 lines, so it was not taken as an answer and was not sent to anyone.", _console.Output);

        _keyboard.Type("yes");
        await ReadsAsync(5);

        Assert.Contains("allow", decision);
        Assert.DoesNotContain(Assert.Single(_rooms.Rooms).Messages, m => m.SenderType == SenderType.Human && m.Content.Contains("rm -rf /"));
        await LeaveAsync(running);
    }

    [Fact(Timeout = Limit)]
    public async Task A_pasted_choice_at_a_pause_is_refused_whole_and_a_typed_choice_still_works()
    {
        var codex = new FakeProvider("codex");
        var provider = new AskingProvider(_ => new FakeSession("@codex\ntake a look"));
        var (_, running) = Start(provider, others: codex);
        await ReadsAsync(1);

        _keyboard.Type("/pause on");
        await ReadsAsync(2);
        _keyboard.Type("@claude @codex agree on this");
        await Poll.UntilAsync(() => _console.Output.Contains("Paused."), "the handover is held");
        await ReadsAsync(3);

        // A paste whose first line would have sent a handover nobody has read.
        _keyboard.Paste("/go", "rm -rf /");
        await ReadsAsync(5);
        AssertShown("That arrived as 2 lines, so it was not used and was not sent to anyone.", _console.Output);
        Assert.Empty(codex.Session.Received);

        _keyboard.Type("/go");
        await ReadsAsync(6);

        Assert.Single(codex.Session.Received);
        await LeaveAsync(running);
    }

    // A read that was interrupted is kept, as a single line always was, and the block it finally
    // returns goes to the next prompt whole. What must not happen is half of it arriving.
    [Fact(Timeout = Limit)]
    public async Task A_block_pasted_after_a_cancelled_pause_reaches_the_room_in_one_piece()
    {
        var codex = new FakeProvider("codex");
        var (command, running) = Start(new AskingProvider(_ => new FakeSession("@codex\ntake a look")), others: codex);
        await ReadsAsync(1);
        _keyboard.Type("/pause on");
        await ReadsAsync(2);
        _keyboard.Type("@claude @codex agree on this");
        await Poll.UntilAsync(() => _console.Output.Contains("Paused."), "the handover is held");
        await ReadsAsync(3);

        // Ctrl+C at the pause. The read begun for it cannot be abandoned, so it is kept.
        Assert.True(command.Interrupt());
        _keyboard.Paste("first", "second");
        await ReadsAsync(5);

        var message = Assert.Single(Assert.Single(_rooms.Rooms).Messages, m => m.SenderType == SenderType.Human && m.Content != "@claude @codex agree on this");
        Assert.Equal("first\nsecond", message.Content);
        Assert.Empty(codex.Session.Received);
        await LeaveAsync(running);
    }

    [Fact(Timeout = Limit)]
    public async Task A_block_pasted_for_a_question_that_was_withdrawn_is_discarded_whole()
    {
        var asked = new TaskCompletionSource<NamedPipeClientStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var turnMayEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new AskingProvider(options => new FakeSession(async (_, _) =>
        {
            var pipe = new NamedPipeClientStream(".", options.ApprovalTool!.Arguments[^1], PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("""{"tool_name":"Bash","input":{"command":"rm -rf build"}}""");
            asked.SetResult(pipe);
            await turnMayEnd.Task;
            return "I went on without it.";
        }));
        var (_, running) = Start(provider);
        await ReadsAsync(1);

        _keyboard.Type("@claude tidy up");
        await using var pipe = await asked.Task.WaitAsync(Patience);
        await ReadsAsync(2);
        turnMayEnd.SetResult();
        using var answer = new StreamReader(pipe, leaveOpen: true);
        Assert.Contains("deny", await answer.ReadLineAsync().WaitAsync(Patience));

        // Pasted by somebody still answering the question Relay has just withdrawn.
        _keyboard.Paste("yes", "and delete the branch");
        await ReadsAsync(4);

        AssertShown("it was not sent to anyone", _console.Output);
        var room = Assert.Single(_rooms.Rooms);
        Assert.DoesNotContain(room.Messages, m => m.SenderType == SenderType.Human && m.Content != "@claude tidy up");
        await LeaveAsync(running);
    }

    [Fact(Timeout = Limit)]
    public async Task A_line_typed_for_a_question_that_was_withdrawn_is_not_sent_to_the_room()
    {
        var asked = new TaskCompletionSource<NamedPipeClientStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var turnMayEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new AskingProvider(options => new FakeSession(async (_, _) =>
        {
            // What the approval tool does for the agent's CLI, which is still waiting when the turn ends.
            var pipe = new NamedPipeClientStream(".", options.ApprovalTool!.Arguments[^1], PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("""{"tool_name":"Bash","input":{"command":"rm -rf build"}}""");
            asked.SetResult(pipe);
            await turnMayEnd.Task;
            return "I went on without it.";
        }));
        var (_, running) = Start(provider);
        await ReadsAsync(1);

        _keyboard.Type("@claude tidy up");
        await using var pipe = await asked.Task.WaitAsync(Patience);
        // The second read is the question's, and it is still waiting for a line when the turn ends.
        await ReadsAsync(2);
        AssertShown("? Claude wants to use Bash:", _console.Output);
        turnMayEnd.SetResult();
        using var answer = new StreamReader(pipe, leaveOpen: true);
        Assert.Contains("deny", await answer.ReadLineAsync().WaitAsync(Patience));

        // The user finishes the answer they were typing and presses Enter.
        _keyboard.Type("yes");
        await ReadsAsync(3);

        AssertShown("it was not sent to anyone", _console.Output);
        Assert.DoesNotContain(Assert.Single(_rooms.Rooms).Messages, m => m.SenderType == SenderType.Human && m.Content == "yes");
        await LeaveAsync(running);
        Assert.False(Directory.Exists(provider.Folder));
    }

    [Fact(Timeout = Limit)]
    public async Task A_question_still_open_when_the_turn_runs_out_of_time_is_withdrawn()
    {
        var asked = new TaskCompletionSource<NamedPipeClientStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new AskingProvider(options => new FakeSession(async (_, _) =>
        {
            var pipe = new NamedPipeClientStream(".", options.ApprovalTool!.Arguments[^1], PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("""{"tool_name":"Bash","input":{"command":"rm -rf build"}}""");
            asked.SetResult(pipe);
            // What the process runner does with the limit it is given: nobody answers, so the time runs out.
            await Task.Delay(options.TurnTimeout!.Value);
            throw new AgentException("'claude' did not finish within 2 seconds and was stopped.") { TimedOut = true };
        }));
        var (_, running) = Start(provider, turnTimeout: "2s");
        await ReadsAsync(1);

        _keyboard.Type("@claude tidy up");
        await using var pipe = await asked.Task.WaitAsync(Patience);
        await ReadsAsync(2);
        AssertShown("? Claude wants to use Bash:", _console.Output);
        using var answer = new StreamReader(pipe, leaveOpen: true);
        Assert.Contains("deny", await answer.ReadLineAsync().WaitAsync(Patience));

        // Typed by somebody who came back to the question after the turn had ended.
        _keyboard.Type("yes");
        await ReadsAsync(3);

        Assert.Equal(TimeSpan.FromSeconds(2), provider.Options!.TurnTimeout);
        AssertShown("Claude failed: 'claude' did not finish within 2 seconds and was stopped.", _console.Output);
        AssertShown("Each turn may run for 2s.", _console.Output);
        AssertShown("it was not sent to anyone", _console.Output);
        Assert.DoesNotContain(Assert.Single(_rooms.Rooms).Messages, m => m.SenderType == SenderType.Human && m.Content == "yes");
        await LeaveAsync(running);
    }

    [Fact(Timeout = Limit)]
    public async Task The_folder_made_for_the_approval_tool_is_removed_when_an_agent_cannot_be_started()
    {
        var provider = new AskingProvider(_ => throw new InvalidOperationException("The session could not be created."));

        var (_, running) = Start(provider);

        await Assert.ThrowsAsync<InvalidOperationException>(() => running.WaitAsync(Patience));
        Assert.False(Directory.Exists(provider.Folder));
    }

    [Fact(Timeout = Limit)]
    public async Task The_folder_made_for_the_approval_tool_is_removed_when_relay_is_told_to_stop()
    {
        var provider = new AskingProvider(_ => new FakeSession("pong"));
        var (command, running) = Start(provider);
        await ReadsAsync(1);
        Assert.True(Directory.Exists(provider.Folder));

        // What the handler for SIGTERM, SIGHUP and a closed console window is given. The process ends after it.
        command.OnTerminating(new PosixSignalContext(PosixSignal.SIGTERM));

        Assert.False(Directory.Exists(provider.Folder));
        await LeaveAsync(running);
    }

    [Fact(Timeout = Limit)]
    public async Task The_folder_made_for_the_approval_tool_is_removed_when_ctrl_c_ends_relay()
    {
        var provider = new AskingProvider(_ => new FakeSession("pong"));
        var (command, running) = Start(provider);
        await ReadsAsync(1);
        Assert.True(Directory.Exists(provider.Folder));

        // With no turn to cancel, Ctrl+C is not held back and the process ends.
        Assert.False(command.Interrupt());

        Assert.False(Directory.Exists(provider.Folder));
        await LeaveAsync(running);
    }
}
