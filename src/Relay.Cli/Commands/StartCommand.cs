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
            var input = Console.ReadLine()?.Trim();
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
                    Reset(room, target: null);
                    break;
                case var command when command.StartsWith("/reset "):
                    Reset(room, command["/reset ".Length..].Trim().TrimStart('@'));
                    break;
                // Only a bare /word is a mistyped command; "/etc/hosts looks wrong" is a message.
                case ['/', .. var name] when name.All(char.IsAsciiLetter):
                    console.MarkupLine($"[yellow]Unknown command {Markup.Escape(input)}. Type /help.[/]");
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
            var result = await router.SendHumanMessageAsync(
                room,
                input,
                agent =>
                {
                    recipient = agent;
                    console.MarkupLine($"[dim]Waiting for {Markup.Escape(agent.DisplayName)}... (Ctrl+C to cancel)[/]");
                },
                turn.Token);

            if (result.Reply is { } reply)
                WriteMessage(room, reply);
            else
                console.MarkupLine("[dim]Recorded. Address an agent to get a reply, e.g. @claude hello[/]");

            if (recipient?.State == AgentSessionState.Faulted)
                console.MarkupLine($"[dim]If this keeps happening, check that {Markup.Escape(recipient.DisplayName)} works when you run it on its own (for example, that you are signed in). /reset {Markup.Escape(recipient.Id)} starts a new conversation with it.[/]");
        }
        finally
        {
            _turn = null;
            _idle.Set();
        }
    }

    private void Reset(ChatRoom room, string? target)
    {
        List<AgentParticipant> targets = target is null
            ? [.. room.Participants.Where(p => p.Session is not null)]
            : room.FindParticipant(target) is { Session: not null } named ? [named] : [];

        if (targets.Count == 0)
        {
            console.MarkupLine("[yellow]No connected agent to reset. Use /reset for all agents or /reset <agent> for one.[/]");
            return;
        }

        foreach (var participant in targets)
        {
            participant.Session!.Reset();
            WriteMessage(room, room.AddMessage(ChatRoom.SystemId, SenderType.System, $"{participant.DisplayName} will start a new conversation with the next message."));
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
        console.MarkupLine("[bold]/reset[/]     Start new agent conversations (/reset <agent> for one)");
        console.MarkupLine("[bold]/exit[/]      Leave the chatroom");
        foreach (var participant in room.Participants)
            console.MarkupLine($"[bold]@{Markup.Escape(participant.Id)} <message>[/]  Send a message to {Markup.Escape(participant.DisplayName)}");
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
                console.MarkupLine($"[bold aqua]{Markup.Escape(name)}:[/] {content}");
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
