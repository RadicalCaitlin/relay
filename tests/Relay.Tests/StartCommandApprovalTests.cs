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
        private readonly BlockingCollection<string?> _typed = new();
        private int _reads;

        public bool IsInputRedirected => false;

        public bool IsOutputRedirected => false;

        /// <summary>How many reads have begun. While the latest waits and no agent is working, Relay writes nothing.</summary>
        public int Reads => Volatile.Read(ref _reads);

        public void DiscardPendingInput()
        {
        }

        public string? ReadLine()
        {
            Interlocked.Increment(ref _reads);
            return _typed.Take();
        }

        public void Type(string? line) => _typed.Add(line);
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

    private (StartCommand Command, Task<int> Running) Start(AskingProvider provider)
    {
        var command = new StartCommand(_console, new AgentDiscoveryService([provider]), _rooms, new MessageRouter(),
            new WorkspaceTrustPrompt(_console, new WorkspaceTrustStore(null), _keyboard), _keyboard);
        // The context is Spectre's own and Relay never reads it.
        return (command, Task.Run(() => command.ExecuteAsync(null!, new StartCommand.Settings { TrustWorkspace = true }, CancellationToken.None)));
    }

    private Task ReadsAsync(int count) => Poll.UntilAsync(() => _keyboard.Reads >= count, $"Relay has read the keyboard {count} times");

    private async Task LeaveAsync(Task<int> running)
    {
        _keyboard.Type("/exit");
        Assert.Equal(0, await running.WaitAsync(Patience));
    }

    [Fact]
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

    [Fact]
    public async Task The_folder_made_for_the_approval_tool_is_removed_when_an_agent_cannot_be_started()
    {
        var provider = new AskingProvider(_ => throw new InvalidOperationException("The session could not be created."));

        var (_, running) = Start(provider);

        await Assert.ThrowsAsync<InvalidOperationException>(() => running.WaitAsync(Patience));
        Assert.False(Directory.Exists(provider.Folder));
    }

    [Fact]
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

    [Fact]
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
