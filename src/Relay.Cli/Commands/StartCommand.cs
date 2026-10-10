using Relay.Cli.Ui;
using Relay.Core.Agents;
using Relay.Core.Rooms;
using Relay.Core.Routing;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Relay.Cli.Commands;

internal sealed class StartCommand(
    IAnsiConsole console,
    AgentDiscoveryService discovery,
    IRoomManager rooms,
    MessageRouter router) : AsyncCommand
{
    private CancellationTokenSource? _turn;

    public override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var room = rooms.CreateRoom("general");
        foreach (var agent in await discovery.DiscoverAllAsync(cancellationToken: cancellationToken))
            room.AddParticipant(AgentParticipant.Create(agent, Environment.CurrentDirectory));

        Console.CancelKeyPress += OnCancelKeyPress;
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
                    console.Clear();
                    WriteHeader(room);
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
        try
        {
            var result = await router.SendHumanMessageAsync(
                room,
                input,
                agent => console.MarkupLine($"[dim]Waiting for {Markup.Escape(agent.DisplayName)}... (Ctrl+C to cancel)[/]"),
                turn.Token);

            if (result.Reply is { } reply)
                WriteMessage(room, reply);
            else
                console.MarkupLine("[dim]Recorded. Address an agent to get a reply, e.g. @claude hello[/]");
        }
        finally
        {
            _turn = null;
        }
    }

    // Ctrl+C cancels the request in flight; with nothing in flight it exits as usual.
    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        if (_turn is not { } turn)
            return;

        e.Cancel = true;
        try
        {
            turn.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void WriteHeader(ChatRoom room)
    {
        console.WriteBanner();
        foreach (var participant in room.Participants)
            console.WriteStatus(participant.DisplayName, participant.Session is not null, Describe(participant.State));

        console.WriteLine();
        console.MarkupLine($"[bold]# {Markup.Escape(room.Name)}[/]  [dim]/help for commands[/]");
        console.WriteLine();
    }

    private void WriteHelp(ChatRoom room)
    {
        console.MarkupLine("[bold]/help[/]      Show this help");
        console.MarkupLine("[bold]/agents[/]    Show discovered agents");
        console.MarkupLine("[bold]/history[/]   Replay the room's message history");
        console.MarkupLine("[bold]/clear[/]     Clear the display (history is kept)");
        console.MarkupLine("[bold]/exit[/]      Leave the chatroom");
        foreach (var participant in room.Participants)
            console.MarkupLine($"[bold]@{Markup.Escape(participant.Id)} <message>[/]  Send a message to {Markup.Escape(participant.DisplayName)}");
    }

    private void WriteMessage(ChatRoom room, ChatMessage message, bool includeHuman = false)
    {
        // Agent output is untrusted: drop control characters so it cannot drive the terminal with escape sequences.
        var printable = string.Concat(message.Content.Where(c => !char.IsControl(c) || c is '\n' or '\t'));
        var content = Markup.Escape(printable);
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
