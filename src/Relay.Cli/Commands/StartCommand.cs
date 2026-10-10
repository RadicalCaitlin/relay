using System.ComponentModel;
using System.Runtime.InteropServices;
using Relay.Cli.Ui;
using Relay.Core.Agents;
using Relay.Core.Rooms;
using Relay.Core.Routing;
using Relay.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Relay.Cli.Commands;

internal sealed class StartCommand(
    IAnsiConsole console,
    AgentDiscoveryService discovery,
    IRoomManager rooms,
    MessageRouter router,
    WorkspaceTrustStore trustStore) : AsyncCommand<StartCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandOption("--trust-workspace")]
        [Description("Trust the current folder for this session only: also load its own agent settings (hooks, MCP servers, CLAUDE.md), which can run commands. Use 'relay trust' to remember it.")]
        public bool TrustWorkspace { get; init; }
    }

    // Windows allows about five seconds between a console-close notification and termination.
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(4);

    private readonly ManualResetEventSlim _idle = new(initialState: true);
    private CancellationTokenSource? _turn;
    private string _folderStatus = string.Empty;
    private bool _pause;
    private Task<string?>? _read;
    private bool _readInterrupted;

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var options = new AgentSessionOptions(Environment.CurrentDirectory, ResolveTrust(settings, Environment.CurrentDirectory));
        var room = rooms.CreateRoom("general");
        foreach (var agent in await discovery.DiscoverAllAsync(cancellationToken: cancellationToken))
            room.AddParticipant(AgentParticipant.Create(agent, options));

        Console.CancelKeyPress += OnCancelKeyPress;
        using var terminated = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnTerminating);
        using var hungUp = PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnTerminating);
        try
        {
            WriteHeader(room);
            await RunLoopAsync(room, cancellationToken);
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            foreach (var participant in room.Participants)
            {
                if (participant.Session is { } session)
                    await session.DisposeAsync();
            }
        }

        return 0;
    }

    // The agent CLIs skip their own trust prompt when run non-interactively, so Relay keeps its own
    // record. A folder is untrusted until the user says otherwise, and a yes is remembered.
    private bool ResolveTrust(Settings settings, string folder)
    {
        var name = RelayConsole.SafeLine(folder);
        var trusted = true;
        if (settings.TrustWorkspace)
            _folderStatus = $"[dim]Folder: {name} (trusted for this session)[/]";
        else if (IsRemembered(folder))
            _folderStatus = $"[dim]Folder: {name} (trusted)[/]";
        else if (AskToTrust(name))
            _folderStatus = Remember(folder)
                ? $"[dim]Folder: {name} (trusted)[/]"
                : $"[dim]Folder: {name} (trusted for this session)[/]";
        else
        {
            trusted = false;
            _folderStatus = $"[yellow]Folder: {name} (not trusted: Claude ignores its settings, hooks, MCP servers and CLAUDE.md, and Codex applies its own trust checks; run 'relay trust' here to change that)[/]";
        }

        return trusted;
    }

    private bool IsRemembered(string folder)
    {
        try
        {
            return trustStore.IsTrusted(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            console.MarkupLine($"[yellow]Could not read the trusted-folder list: {RelayConsole.SafeLine(ex.Message)}[/]");
            return false;
        }
    }

    // Without a person who can both see the question and answer it, the folder stays untrusted.
    private bool AskToTrust(string name)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected || !console.Profile.Capabilities.Interactive)
            return false;

        console.MarkupLine($"Agents will run in [bold]{name}[/].");
        console.MarkupLine("[dim]A trusted folder can configure Claude: its hooks and MCP servers run commands on your machine. Codex applies its own trust checks either way.[/]");
        var trusted = console.Confirm("Do you trust the files in this folder?", defaultValue: false);
        console.WriteLine();
        return trusted;
    }

    private bool Remember(string folder)
    {
        try
        {
            trustStore.Trust(folder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            console.MarkupLine($"[yellow]Could not remember this choice: {RelayConsole.SafeLine(ex.Message)}[/]");
            return false;
        }
    }

    private async Task RunLoopAsync(ChatRoom room, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            console.Markup("[bold green]You:[/] ");
            var input = ReadLine();
            if (input is null)
                return;
            if (input.Length == 0)
                continue;

            switch (input.ToLowerInvariant())
            {
                case "/exit" or "/quit":
                    return;
                case "/help":
                    WriteHelp(room);
                    break;
                case "/agents":
                    console.WriteAgentTable(await discovery.DiscoverAllAsync(includeVersion: true, cancellationToken));
                    break;
                case "/history":
                    foreach (var message in room.Messages)
                        WriteMessage(room, message, includeHuman: true);
                    break;
                case "/clear":
                    // There is no screen to clear when output goes to a file or a pipe, and trying to throws.
                    if (!Console.IsOutputRedirected)
                        console.Clear();
                    WriteHeader(room);
                    break;
                case "/reset":
                    Reset(room, target: null, keepRoom: false);
                    break;
                case var command when command.StartsWith("/reset "):
                    Reset(room, command["/reset ".Length..].Trim().TrimStart('@'), keepRoom: false);
                    break;
                // Never a toggle: typing it twice must not quietly turn a safety setting off.
                case "/pause":
                    console.MarkupLine($"[dim]Pause mode is {(_pause ? "on" : "off")}. Use /pause on or /pause off.[/]");
                    break;
                case "/pause on":
                    _pause = true;
                    console.MarkupLine("[dim]Pause mode is on: when one agent addresses another, Relay asks you before passing the message on.[/]");
                    break;
                case "/pause off":
                    _pause = false;
                    console.MarkupLine("[dim]Pause mode is off: agents you name together pass messages to each other without asking.[/]");
                    break;
                case var command when command.StartsWith("/pause "):
                    console.MarkupLine($"[yellow]Pause mode is still {(_pause ? "on" : "off")}. Use /pause on or /pause off.[/]");
                    break;
                case "/restart":
                    Reset(room, target: null, keepRoom: true);
                    break;
                case var command when command.StartsWith("/restart "):
                    Reset(room, command["/restart ".Length..].Trim().TrimStart('@'), keepRoom: true);
                    break;
                // A line whose first word is /word was meant as a command, with or without arguments, and a
                // mistyped one must not be recorded for every agent to read. "/etc/hosts looks wrong" is a message.
                case var command when command.Split([' ', '\t'], 2)[0] is ['/', .. var name] && name.All(char.IsAsciiLetter):
                    console.MarkupLine($"[yellow]Unknown command {Markup.Escape(input)}. Type /help. A line that starts with /word is never sent as a message.[/]");
                    break;
                default:
                    await SendAsync(room, input, cancellationToken);
                    break;
            }

            console.WriteLine();
        }
    }

    private async Task SendAsync(ChatRoom room, string input, CancellationToken cancellationToken)
    {
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _turn = turn;
        _idle.Reset();
        try
        {
            AgentParticipant? recipient = null;
            var events = new ExchangeEvents
            {
                TurnStarting = starting =>
                {
                    recipient = starting.Agent;
                    var name = Markup.Escape(starting.Agent.DisplayName);
                    console.MarkupLine(starting.TurnLimit > 1
                        ? $"[dim]Waiting for {name}... (turn {starting.Turn} of {starting.TurnLimit}, Ctrl+C to stop the exchange)[/]"
                        : $"[dim]Waiting for {name}... (Ctrl+C to cancel)[/]");
                },
                Recorded = message => WriteMessage(room, message),
                Pause = _pause ? (handover, addressee) => AskAboutHandover(room, handover, addressee, turn.Token) : null,
            };

            // One token for the whole exchange, so Ctrl+C between two agents' turns still stops it.
            var result = await router.SendHumanMessageAsync(room, input, events, turn.Token);

            if (result.Reason == StopReason.NotAddressed)
                console.MarkupLine("[dim]Recorded. Agents will see it the next time you address them, e.g. @claude hello[/]");

            if (recipient?.State == AgentSessionState.Faulted)
                console.MarkupLine($"[dim]If this keeps happening, check that {Markup.Escape(recipient.DisplayName)} works when you run it on its own (for example, that you are signed in). /restart {Markup.Escape(recipient.Id)} starts a new conversation with it and shows it the room again.[/]");
        }
        finally
        {
            _turn = null;
            _idle.Set();
        }
    }

    // A read of the console cannot be abandoned. One that was interrupted is kept, and the line it
    // eventually returns answers the next prompt instead of being lost.
    /// <exception cref="OperationCanceledException">Cancelled while waiting for a line.</exception>
    private string? ReadLine(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var read = _read ??= Task.Run(Console.ReadLine);
            try
            {
                read.Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _readInterrupted = true;
                throw;
            }

            _read = null;
            var interrupted = _readInterrupted;
            _readInterrupted = false;

            // On Windows, Ctrl+C makes the read in progress return as if input had ended. After an
            // interrupted read that is not the end of input, so read again.
            var line = read.Result;
            if (line is null && interrupted)
                continue;

            return line?.Trim();
        }
    }

    // Pause mode: a handover waits here until the user sends it on, stops, or asks its author for another.
    private HandoverDecision AskAboutHandover(ChatRoom room, ChatMessage handover, AgentParticipant addressee, CancellationToken cancellationToken)
    {
        var author = Markup.Escape(room.FindParticipant(handover.SenderId)?.DisplayName ?? handover.SenderId);
        var recipient = Markup.Escape(addressee.DisplayName);
        while (true)
        {
            console.MarkupLine($"[dim]Paused. /go sends this to {recipient}, /stop ends the exchange, anything else goes back to {author} as your message.[/]");
            console.Markup("[bold green]You:[/] ");

            // No agent is running while the question is open, so a shutdown has nothing to wait for.
            string? input;
            _idle.Set();
            try
            {
                input = ReadLine(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                console.WriteLine();
                return new HandoverDecision.Stop();
            }
            finally
            {
                _idle.Reset();
            }

            if (input is null)
                return new HandoverDecision.Stop();

            switch (input.ToLowerInvariant())
            {
                case "/stop":
                    return new HandoverDecision.Stop();
                case "/go":
                    return new HandoverDecision.Deliver();
                // An empty line never sends: a key pressed during the turn before must not approve a message nobody has read.
                case "":
                    break;
                // Anything that looks like a command is never sent to an agent: "/go ahead" must not cost a turn.
                case ['/', ..]:
                    console.MarkupLine($"[yellow]{Markup.Escape(input)} is not a command here. Type /go or /stop on its own, or a message for {author} that does not start with /.[/]");
                    break;
                // Nor is a line addressed to somebody else, which was probably meant to send the handover on.
                case var _ when Mention.TryParse(input, out var target, out _) && room.FindParticipant(target) is { } named && named.Id != handover.SenderId:
                    console.MarkupLine($"[yellow]What you type here goes to {author}, not to {Markup.Escape(named.DisplayName)}. Type /go to send {author}'s message on.[/]");
                    break;
                default:
                    return new HandoverDecision.SendBack(input);
            }
        }
    }

    // /reset is a clean slate; /restart drops the agent's own conversation but shows it the room again.
    private void Reset(ChatRoom room, string? target, bool keepRoom)
    {
        var command = keepRoom ? "/restart" : "/reset";
        List<AgentParticipant> targets = target is null
            ? [.. room.Participants.Where(p => p.Session is not null)]
            : room.FindParticipant(target) is { Session: not null } named ? [named] : [];

        if (targets.Count == 0)
        {
            console.MarkupLine($"[yellow]No connected agent found. Use {command} for all agents or {command} <agent> for one.[/]");
            return;
        }

        foreach (var participant in targets)
        {
            if (keepRoom)
                room.RestartConversation(participant);
            else
                room.ResetConversation(participant);

            var notice = keepRoom
                ? $"{participant.DisplayName} will start a new conversation with the next message, and will be shown the recent messages in this room again."
                : $"{participant.DisplayName} will start a new conversation with the next message, and will not be shown anything said before now.";
            WriteMessage(room, room.AddMessage(ChatRoom.SystemId, SenderType.System, notice));
        }
    }

    // Ctrl+C cancels the request in flight; with nothing in flight it exits as usual.
    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        => e.Cancel = CancelTurn();

    // Relay is being closed (terminal closed, kill, logoff). The agent CLI would otherwise outlive it, so
    // stop the request in flight and hold the shutdown until its process tree is gone. The signal is not
    // cancelled, so Relay still exits afterwards.
    private void OnTerminating(PosixSignalContext context)
    {
        if (CancelTurn())
            _idle.Wait(ShutdownGrace);
    }

    private bool CancelTurn()
    {
        if (_turn is not { } turn)
            return false;

        try
        {
            turn.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        return true;
    }

    private void WriteHeader(ChatRoom room)
    {
        console.WriteBanner();
        foreach (var participant in room.Participants)
            console.WriteStatus(participant.DisplayName, participant.Session is not null, Describe(participant.State));

        console.WriteLine();
        console.MarkupLine(_folderStatus);
        console.MarkupLine($"[bold]# {Markup.Escape(room.Name)}[/]  [dim]/help for commands[/]");
        console.WriteLine();
    }

    private void WriteHelp(ChatRoom room)
    {
        console.MarkupLine("[bold]/help[/]      Show this help");
        console.MarkupLine("[bold]/agents[/]    Show discovered agents");
        console.MarkupLine("[bold]/history[/]   Replay the room's message history");
        console.MarkupLine("[bold]/clear[/]     Clear the display (history is kept)");
        console.MarkupLine("[bold]/restart[/]   Start new agent conversations, keeping what was said in the room (/restart <agent> for one)");
        console.MarkupLine("[bold]/reset[/]     Start new agent conversations from a clean slate (/reset <agent> for one)");
        console.MarkupLine($"[bold]/pause[/]     Ask before one agent's message is passed to another: /pause on or /pause off (now {(_pause ? "on" : "off")})");
        console.MarkupLine("[bold]/exit[/]      Leave the chatroom");
        foreach (var participant in room.Participants)
            console.MarkupLine($"[bold]@{Markup.Escape(participant.Id)} <message>[/]  Send a message to {Markup.Escape(participant.DisplayName)}");
        if (room.Participants.Where(p => p.Session is not null).ToList() is [var first, var second, ..])
            console.MarkupLine($"[bold]@{Markup.Escape(first.Id)} @{Markup.Escape(second.Id)} <message>[/]  Send it to {Markup.Escape(first.DisplayName)}, and let the agents named pass messages to each other. Only the first one is asked; the others speak if they are addressed.");
        console.MarkupLine("[dim]Everything said in this room, by you or by an agent, is shown to each agent the next time you address it.[/]");
    }

    private void WriteMessage(ChatRoom room, ChatMessage message, bool includeHuman = false)
    {
        var content = RelayConsole.Safe(message.Content);
        switch (message.SenderType)
        {
            case SenderType.Human when includeHuman:
                console.MarkupLine($"[bold green]You:[/] {content}");
                break;
            case SenderType.Agent:
                var name = room.FindParticipant(message.SenderId)?.DisplayName ?? message.SenderId;
                var to = message.TargetId is { } id && room.FindParticipant(id) is { } addressee ? $" to {addressee.DisplayName}" : string.Empty;
                console.MarkupLine($"[bold aqua]{Markup.Escape(name + to)}:[/] {content}");
                break;
            case SenderType.System:
                console.MarkupLine($"[yellow]! {content}[/]");
                break;
        }
    }

    private static string Describe(AgentSessionState state) => state switch
    {
        AgentSessionState.Ready => "connected",
        AgentSessionState.Busy => "busy",
        AgentSessionState.Faulted => "last request failed",
        AgentSessionState.Closed => "closed",
        _ => "unavailable",
    };
}
