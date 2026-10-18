using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Relay.Cli.Approvals;
using Relay.Cli.Infrastructure;
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
    WorkspaceTrustPrompt trustPrompt,
    IConsoleStreams streams) : AsyncCommand<StartCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandOption("--trust-workspace")]
        [Description("Trust the current folder for this session only: also load its own agent settings (hooks, MCP servers, CLAUDE.md), which can run commands. Use 'relay trust' to remember it.")]
        public bool TrustWorkspace { get; init; }

        [CommandOption("--workspace <PATH>")]
        [Description("The folder agents work in. Defaults to the current folder.")]
        public string? Workspace { get; init; }

        [CommandOption("--agents <IDS>")]
        [Description("Only these agents join the room, separated by commas, such as claude,codex. Each must be installed. Defaults to every agent Relay knows.")]
        public string? Agents { get; init; }

        [CommandOption("--turn-timeout <TIME>")]
        [Description("Stop an agent whose turn runs longer than this, such as 90s, 30m or 2h. Off by default: a turn runs until it ends or you press Ctrl+C.")]
        public string? TurnTimeout { get; init; }

        // The bound is stated here as a number because an attribute takes only constant text.
        [CommandOption("--max-turns <N>")]
        [Description("How many agent turns one exchange may run, from 1 to 20. Defaults to 6. Only you can set it, only here, and it holds for the whole session.")]
        public string? MaxTurns { get; init; }

        public override ValidationResult Validate()
        {
            if (TurnTimeout is not null && !TryParseTime(TurnTimeout, out _))
                return ValidationResult.Error("--turn-timeout must be a whole number of seconds, minutes or hours, such as 90s, 30m or 2h, and no more than 24h.");
            if (MaxTurns is not null && !TryParseTurns(MaxTurns, out _))
                return ValidationResult.Error($"--max-turns must be a whole number from 1 to {ExchangeLimits.MaxAllowedTurns}, such as --max-turns 10.");
            if (Workspace is not null)
            {
                if (ResolveWorkspace(Workspace) is not { } folder)
                    return ValidationResult.Error($"--workspace must name a folder that exists. Not found: {RelayConsole.PlainLine(Workspace)}");
                WorkspacePath = folder;
            }

            if (Agents is not null && AgentIds(Agents).Count == 0)
                return ValidationResult.Error("--agents must name at least one agent, such as --agents claude or --agents claude,codex.");
            return ValidationResult.Success();
        }

        /// <summary>The folder <see cref="Workspace"/> names, as resolved when the options were checked.</summary>
        internal string? WorkspacePath { get; private set; }

        /// <summary>The folder as a full path, resolved against the current folder, or null if there is no such folder.</summary>
        internal static string? ResolveWorkspace(string path)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (ArgumentException)
            {
                return null;
            }

            if (OperatingSystem.IsWindows())
                return Directory.Exists(full) ? Path.TrimEndingDirectorySeparator(full) : null;

            // The current folder comes from the system with every link resolved. A folder named here is
            // resolved the same way, or the trust list would know one folder by two names.
            return RealPath(full) is { } real && Directory.Exists(real) ? real : null;
        }

        private static string? RealPath(string path)
        {
            var real = realpath(path, IntPtr.Zero);
            if (real == IntPtr.Zero)
                return null;

            try
            {
                return Marshal.PtrToStringUTF8(real);
            }
            finally
            {
                free(real);
            }
        }

        [DllImport("libc")]
        private static extern IntPtr realpath(string path, IntPtr resolved);

        [DllImport("libc")]
        private static extern void free(IntPtr pointer);

        internal static IReadOnlyList<string> AgentIds(string text)
            => [.. text.Split(',', StringSplitOptions.TrimEntries)
                .Select(id => id.TrimStart('@').ToLowerInvariant())
                .Where(id => id.Length > 0)
                .Distinct()];

        internal static bool TryParseTime(string text, out TimeSpan time)
        {
            time = default;
            if (text.Length < 2 || !int.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count == 0)
                return false;

            long unit = char.ToLowerInvariant(text[^1]) switch
            {
                's' => 1,
                'm' => 60,
                'h' => 3600,
                _ => 0,
            };
            var seconds = count * unit;
            if (seconds is 0 or > 24 * 3600)
                return false;

            time = TimeSpan.FromSeconds(seconds);
            return true;
        }

        // Digits only: a sign, a decimal point, spaces or a number too large for an int are all
        // refused rather than rounded or wrapped into something Relay would then run.
        internal static bool TryParseTurns(string text, out int turns)
            => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out turns)
                && turns >= 1
                && turns <= ExchangeLimits.MaxAllowedTurns;
    }

    // Windows allows about five seconds between a console-close notification and termination.
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(4);

    // Characters cmd.exe gives a meaning to even inside quotes. The agent CLIs are cmd.exe shims on
    // Windows, and the folder below is named on their command line.
    private static readonly char[] UnsafeOnCommandLine = ['%', '!', '^', '&', '|', '<', '>', '"'];

    private const string NoTerminal = "Relay asks you only when it is run in a terminal";

    private readonly ManualResetEventSlim _idle = new(initialState: true);
    private readonly Lock _writing = new();
    private readonly PastedInput _input = new(streams, TimeProvider.System);
    private CancellationTokenSource? _turn;
    private string _folderStatus = string.Empty;
    private string? _turnTimeout;
    // Settled before the room opens and never written again: nothing in the session can raise it.
    private int _maxTurns = ExchangeLimits.DefaultTurns;
    private bool _maxTurnsChosen;
    private string _workspace = string.Empty;
    private bool _pause;
    // Read by a session when its turn starts, on whichever thread runs it.
    private volatile bool _auto;
    private Task<TypedInput?>? _read;
    private bool _readInterrupted;
    private bool _readWithdrawn;
    private ApprovalDesk? _desk;
    private string? _noApprovals = NoTerminal;
    private string? _approvalFolder;
    private CancellationTokenSource? _question;
    private volatile AskingTurn? _asking;

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var workspace = _workspace = settings.WorkspacePath ?? Environment.CurrentDirectory;
        var agents = await discovery.DiscoverAllAsync(cancellationToken: cancellationToken);
        if (settings.Agents is { } chosen)
        {
            var ids = Settings.AgentIds(chosen);
            if (ids.FirstOrDefault(id => agents.All(a => a.Provider.Id != id)) is { } unknown)
            {
                console.MarkupLine($"[red]--agents: Relay has no agent called {RelayConsole.SafeLine(unknown)}. Agents it knows: {Markup.Escape(string.Join(", ", agents.Select(a => a.Provider.Id)))}.[/]");
                return 1;
            }

            agents = [.. agents.Where(a => ids.Contains(a.Provider.Id))];
            if (agents.FirstOrDefault(a => !a.Result.IsInstalled) is { } missing)
            {
                console.MarkupLine($"[red]--agents: {Markup.Escape(missing.Result.DisplayName)} is not available. {RelayConsole.Safe(missing.Result.Detail ?? string.Empty)}[/]");
                return 1;
            }
        }

        var trust = trustPrompt.Resolve(settings.TrustWorkspace, workspace);
        _folderStatus = trust.Status;
        _turnTimeout = settings.TurnTimeout;
        TimeSpan? turnTimeout = settings.TurnTimeout is { } text && Settings.TryParseTime(text, out var time) ? time : null;

        // The router is built here, once, with the limit the user asked for at start-up. There is
        // nowhere to set it afterwards, so no command, no agent and nothing in the workspace can
        // change how long an exchange may run (ADR 0002).
        _maxTurnsChosen = settings.MaxTurns is not null;
        if (settings.MaxTurns is { } chosenTurns && Settings.TryParseTurns(chosenTurns, out var turns))
            _maxTurns = turns;
        var router = new MessageRouter(limits: new ExchangeLimits(_maxTurns));

        // Without a person who can both see a question and answer it, nothing is put to anyone,
        // and an agent's CLI refuses what its own settings do not allow, as it always has.
        ApprovalServer? approvals = null;
        var room = rooms.CreateRoom("general");
        Console.CancelKeyPress += OnCancelKeyPress;
        using var terminated = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnTerminating);
        using var hungUp = PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnTerminating);
        try
        {
            if (!streams.IsInputRedirected && !streams.IsOutputRedirected && console.Profile.Capabilities.Interactive)
            {
                var folder = _approvalFolder = Directory.CreateTempSubdirectory("relay-approval-").FullName;
                if (folder.IndexOfAny(UnsafeOnCommandLine) >= 0)
                {
                    _noApprovals = "the path of your temporary folder has a character in it that is not safe on a command line";
                }
                else
                {
                    var desk = new ApprovalDesk(console, _writing, () => _asking, ReadAnswer, DiscardTypedAhead);
                    var server = new ApprovalServer(desk.Decide);
                    try
                    {
                        server.Start();
                        _desk = desk;
                        approvals = server;
                        _noApprovals = null;
                    }
                    catch (IOException ex)
                    {
                        _noApprovals = $"the pipe Relay needs for it could not be opened ({RelayConsole.SafeLine(ex.Message)})";
                    }
                }
            }

            var options = new AgentSessionOptions(workspace, trust.Trusted,
                approvals is null ? null : ApprovalToolFor(approvals.PipeName, _approvalFolder!), turnTimeout, () => _auto);
            foreach (var agent in agents)
                room.AddParticipant(AgentParticipant.Create(agent, options));

            WriteHeader(room);
            await RunLoopAsync(room, router, cancellationToken);
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            foreach (var participant in room.Participants)
            {
                if (participant.Session is { } session)
                    await session.DisposeAsync();
            }

            if (approvals is not null)
                await approvals.DisposeAsync();
            RemoveApprovalFolder();
        }

        return 0;
    }

    // Relay itself, run again as the program an agent's CLI starts in order to ask. Under "dotnet
    // relay.dll" the process is dotnet, so the assembly has to be named as well.
    // Called on every way out, including the ones that end the process without unwinding it.
    private void RemoveApprovalFolder()
    {
        if (Interlocked.Exchange(ref _approvalFolder, null) is not { } folder)
            return;

        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Relay is leaving either way, and the system clears its temporary folder in time.
        }
    }

    private static ApprovalTool ApprovalToolFor(string pipeName, string folder)
    {
        var (host, arguments) = RelayExecutable.With(ApprovalBridge.Command, pipeName);
        return new ApprovalTool(host, arguments, folder);
    }

    private async Task RunLoopAsync(ChatRoom room, MessageRouter router, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            console.Markup("[bold green]You:[/] ");
            if (ReadLine() is not { } typed)
                return;
            // A blank line has always been skipped, and a paste of nothing but blank lines is still
            // nothing: it must not be recorded as a message for every agent to be shown.
            if (string.IsNullOrWhiteSpace(typed.Text))
                continue;

            // Only the first line decides what a message is, so a line of a paste is never run as a
            // command and never addresses anyone. The rest is text.
            var input = FirstLine(typed.Text);
            var extra = typed.Lines - 1;
            if (extra > 0 && IsCommand(input))
                console.MarkupLine($"[yellow]Only the first line was read as a command. The other {extra} line{(extra == 1 ? " was" : "s were")} not used.[/]");

            switch (input.ToLowerInvariant())
            {
                case "/exit" or "/quit":
                    return;
                case "/help":
                    WriteHelp(room);
                    break;
                case "/agents":
                    var discovered = await discovery.DiscoverAllAsync(includeVersion: true, cancellationToken);
                    console.WriteAgentTable([.. discovered.Where(a => room.FindParticipant(a.Provider.Id) is not null)]);
                    break;
                case "/history":
                    foreach (var message in room.Messages)
                        WriteMessage(room, message, includeHuman: true);
                    break;
                case "/clear":
                    // There is no screen to clear when output goes to a file or a pipe, and trying to throws.
                    if (!streams.IsOutputRedirected)
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
                // Never a toggle either, for the same reason.
                case "/approvals":
                    console.MarkupLine($"[dim]{DescribeApprovals(room)}{(_desk is null ? string.Empty : " Use /approvals on or /approvals off.")}[/]");
                    // So the user knows why so few questions come.
                    if (_auto)
                        console.MarkupLine($"[dim]{DescribeAuto(room)}[/]");
                    break;
                case "/approvals on" or "/approvals off" when _desk is null:
                    console.MarkupLine($"[yellow]{DescribeApprovals(room)}[/]");
                    break;
                case "/approvals on":
                    _desk!.Enabled = true;
                    console.MarkupLine($"[dim]{DescribeApprovals(room)}[/]");
                    break;
                case "/approvals off":
                    _desk!.Enabled = false;
                    console.MarkupLine($"[dim]{DescribeApprovals(room)}[/]");
                    break;
                case var command when command.StartsWith("/approvals "):
                    console.MarkupLine($"[yellow]{DescribeApprovals(room)} Use /approvals on or /approvals off.[/]");
                    break;
                // Never a toggle either. Commands are only read between turns, so a change always
                // applies from the next one and never to a turn or an exchange in progress.
                case "/auto":
                    console.MarkupLine($"[dim]{DescribeAuto(room)} Use /auto on or /auto off.[/]");
                    break;
                // Left off, so that nothing later says it is on, or turns it on for an agent added after.
                case "/auto on" when !HasAutoMode(room):
                    console.MarkupLine($"[yellow]{DescribeAuto(room)}[/]");
                    break;
                case "/auto on":
                    _auto = true;
                    console.MarkupLine($"[dim]{DescribeAuto(room)}[/]");
                    break;
                case "/auto off":
                    _auto = false;
                    console.MarkupLine($"[dim]{DescribeAuto(room)}[/]");
                    break;
                case var command when command.StartsWith("/auto "):
                    console.MarkupLine($"[yellow]Auto mode is still {(_auto ? "on" : "off")}. Use /auto on or /auto off.[/]");
                    break;
                case "/role":
                    WriteRoles(room);
                    break;
                // From the line as typed: a description of the user's own keeps its capitals.
                case var command when command.StartsWith("/role "):
                    SetRole(room, input["/role ".Length..].Trim());
                    break;
                case "/restart":
                    Reset(room, target: null, keepRoom: true);
                    break;
                case var command when command.StartsWith("/restart "):
                    Reset(room, command["/restart ".Length..].Trim().TrimStart('@'), keepRoom: true);
                    break;
                case var command when IsCommand(command):
                    console.MarkupLine($"[yellow]Unknown command {Markup.Escape(input)}. Type /help. A line that starts with /word is never sent as a message.[/]");
                    break;
                default:
                    // Said before the message goes anywhere, so a paste that went wrong is visible.
                    if (typed.Lines > 1)
                        console.MarkupLine($"[dim]Sending {typed.Lines} lines as one message.[/]");
                    await SendAsync(room, router, typed.Text, cancellationToken);
                    break;
            }

            console.WriteLine();
        }
    }

    // Never trimmed as a whole before this: a block whose first line is blank must not promote its
    // second line into a command or an address.
    private static string FirstLine(string text)
        => (text.IndexOf('\n') is var end && end < 0 ? text : text[..end]).Trim();

    // A line whose first word is /word was meant as a command, with or without arguments, and a
    // mistyped one must not be recorded for every agent to read. "/etc/hosts looks wrong" is a message.
    private static bool IsCommand(string line)
        => line.Split([' ', '\t'], 2)[0] is ['/', .. var name] && name.All(char.IsAsciiLetter);

    private async Task SendAsync(ChatRoom room, MessageRouter router, string input, CancellationToken cancellationToken)
    {
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _turn = turn;
        _idle.Reset();
        try
        {
            AgentParticipant? recipient = null;
            // Said at most once per message: in an exchange every turn of the same agent would say it again.
            var modeSeen = false;
            var modeWarned = false;
            var events = new ExchangeEvents
            {
                TurnStarting = starting =>
                {
                    recipient = starting.Agent;
                    modeSeen = false;
                    StopAsking();
                    if (starting.Agent.AsksForApproval)
                    {
                        _question = CancellationTokenSource.CreateLinkedTokenSource(turn.Token);
                        _asking = new(starting.Agent.DisplayName, _question.Token);
                    }

                    var name = Markup.Escape(starting.Agent.DisplayName);
                    // The count is shown in an exchange even when the limit is one, so the user can
                    // see at once why a reply to another agent went nowhere.
                    console.MarkupLine(starting.InExchange
                        ? $"[dim]Waiting for {name}... (turn {starting.Turn} of {starting.TurnLimit}, Ctrl+C to stop the exchange)[/]"
                        : $"[dim]Waiting for {name}... (Ctrl+C to cancel)[/]");
                },
                Activity = (agent, activity) =>
                {
                    if (activity.Kind != AgentActivityKind.Mode)
                    {
                        WriteActivity(activity);
                        return;
                    }

                    modeSeen = true;
                    if (!modeWarned && _auto && agent.HasAutoMode && activity.Text != "auto")
                    {
                        modeWarned = true;
                        WarnAboutMode($"{Markup.Escape(agent.DisplayName)} could not use auto mode and ran this turn in {RelayConsole.ActivityLine(activity.Text, _workspace)} mode");
                    }
                },
                Recorded = message =>
                {
                    // A reply with no mode reported before it: nothing confirms that auto mode was used.
                    if (!modeSeen && !modeWarned && _auto && message.SenderType == SenderType.Agent && room.FindParticipant(message.SenderId) is { HasAutoMode: true } agent)
                    {
                        modeWarned = true;
                        WarnAboutMode($"{Markup.Escape(agent.DisplayName)} did not say which mode it ran this turn in, so Relay cannot tell whether it used auto mode");
                    }

                    WriteMessage(room, message);
                },
                Pause = _pause
                    ? (handover, addressee) =>
                    {
                        StopAsking();
                        return AskAboutHandover(room, handover, addressee, turn.Token);
                    }
                    : null,
            };

            // One token for the whole exchange, so Ctrl+C between two agents' turns still stops it.
            var result = await router.SendHumanMessageAsync(room, input, events, turn.Token);

            if (result.Reason == StopReason.NotAddressed)
                console.MarkupLine("[dim]Recorded. Agents will see it the next time you address them, e.g. @claude hello[/]");

            // Said after the router's own notice, which names the agents to carry on with.
            if (result.Reason == StopReason.TurnLimit)
                console.MarkupLine(_maxTurns < ExchangeLimits.MaxAllowedTurns
                    ? $"[dim]An exchange is at most {_maxTurns} agent turns. To allow more, start Relay with a larger --max-turns, up to {ExchangeLimits.MaxAllowedTurns}.[/]"
                    : $"[dim]An exchange is at most {_maxTurns} agent turns, the most Relay allows.[/]");

            if (result.Reason == StopReason.TimedOut)
                console.MarkupLine($"[dim]Each turn may run for {Markup.Escape(_turnTimeout!)}. To allow longer turns, start Relay with a larger --turn-timeout, or without it for no limit. {Markup.Escape(recipient!.DisplayName)} keeps its conversation.[/]");
            else if (recipient?.State == AgentSessionState.Faulted)
            {
                console.MarkupLine($"[dim]If this keeps happening, check that {Markup.Escape(recipient.DisplayName)} works when you run it on its own (for example, that you are signed in). /restart {Markup.Escape(recipient.Id)} starts a new conversation with it and shows it the room again.[/]");
                // A Claude Code from before auto mode refuses the argument, so every turn fails.
                if (_auto && recipient.HasAutoMode)
                    console.MarkupLine($"[dim]If this began after /auto on, your {Markup.Escape(recipient.DisplayName)} may be too old to have an auto mode: /auto off runs it as before.[/]");
            }
        }
        finally
        {
            StopAsking();
            _turn = null;
            _idle.Set();
        }
    }

    // The turn a question belonged to is over: it can no longer be answered, and whatever is
    // typed next is for the room. Returns once the question has let go of the keyboard.
    private void StopAsking()
    {
        _asking = null;
        var question = Interlocked.Exchange(ref _question, null);
        question?.Cancel();
        _desk?.WaitUntilIdle();
        question?.Dispose();
    }

    private TypedInput? ReadAnswer(CancellationToken closed)
    {
        try
        {
            return ReadLine(closed);
        }
        catch (OperationCanceledException)
        {
            // The read goes on, and what it returns was typed, at least in part, as an answer to
            // this question. It must not become a message for the room or an answer to another.
            _readWithdrawn = true;
            lock (_writing)
                console.WriteLine();
            return null;
        }
    }

    // A key pressed before a question was shown must not answer it. A line left over from a read
    // that was interrupted is dropped, and so is anything typed that no read has taken yet.
    private void DiscardTypedAhead()
    {
        if (_read is { IsCompleted: true })
        {
            _read = null;
            _readInterrupted = false;
            _readWithdrawn = false;
        }

        if (_read is null)
            streams.DiscardPendingInput();
    }

    private string DescribeApprovals(ChatRoom room)
    {
        if (_desk is null)
            return $"Approvals are not available: {_noApprovals}, so a step an agent's own settings do not allow is refused.";
        if (!_desk.Enabled)
            return "Approvals are off: nothing is put to you, and a step an agent's own settings do not allow is refused.";

        var connected = room.Participants.Where(p => p.Session is not null).ToList();
        var asking = string.Join(" and ", connected.Where(p => p.AsksForApproval).Select(p => Markup.Escape(p.DisplayName)));
        var silent = string.Join(" and ", connected.Where(p => !p.AsksForApproval).Select(p => Markup.Escape(p.DisplayName)));
        var asks = asking.Length > 0
            ? $"Approvals are on: {asking} asks you before a step its own settings do not allow, and can ask you questions."
            : "Approvals are on, but no agent here can ask.";
        return silent.Length > 0 ? $"{asks} {silent} cannot ask: a step its own settings do not allow is refused." : asks;
    }

    private static bool HasAutoMode(ChatRoom room) => room.Participants.Any(p => p.HasAutoMode && p.Session is not null);

    private string DescribeAuto(ChatRoom room)
    {
        var connected = room.Participants.Where(p => p.Session is not null).ToList();
        var auto = string.Join(" and ", connected.Where(p => p.HasAutoMode).Select(p => Markup.Escape(p.DisplayName)));
        if (auto.Length == 0)
            return "Auto mode is not available: no agent here has one.";

        var without = string.Join(", ", connected.Where(p => !p.HasAutoMode).Select(p => Markup.Escape(p.DisplayName)));
        var unaffected = without.Length > 0 ? $" Not affected, having no auto mode: {without}." : string.Empty;
        return _auto
            ? $"Auto mode is on for {auto}: its classifier decides the steps its settings leave open, and only what it will not decide is put to you.{unaffected}"
            : $"Auto mode is off for {auto}: its own settings decide which steps it may take.{unaffected}";
    }

    // Said only, never acted on: the mode comes from the agent's own output, and is shown through
    // ActivityLine like any other activity. With auto mode off the agent's settings decide.
    private void WarnAboutMode(string what)
    {
        lock (_writing)
            console.MarkupLine($"[yellow]  ! {what}, so steps its own settings do not allow may be put to you, or refused if nobody can be asked.[/]");
    }

    // A read of the console cannot be abandoned. One that was interrupted is kept, and the line it
    // eventually returns answers the next prompt instead of being lost. The exception is a read
    // begun for a question that has been withdrawn: its line is said not to have gone anywhere.
    // The whole of a pasted block is collected inside this one task, so a block is never half read
    // and its later lines can never be left behind for the next prompt to pick up.
    /// <exception cref="OperationCanceledException">Cancelled while waiting for a line.</exception>
    private TypedInput? ReadLine(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var read = _read ??= Task.Run(_input.Read);
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
            var withdrawn = _readWithdrawn;
            _readInterrupted = false;
            _readWithdrawn = false;

            // On Windows, Ctrl+C makes the read in progress return as if input had ended. After an
            // interrupted read that is not the end of input, so read again.
            var line = read.Result;
            if (line is null && interrupted)
                continue;

            if (withdrawn && !string.IsNullOrWhiteSpace(line?.Text))
            {
                lock (_writing)
                {
                    console.MarkupLine("[yellow]The question was withdrawn while that line was being typed, so it was not sent to anyone. Type it again if you still mean it.[/]");
                    console.Markup("[bold green]You:[/] ");
                }

                continue;
            }

            return line;
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
            TypedInput? typed;
            _idle.Set();
            try
            {
                typed = ReadLine(cancellationToken);
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

            if (typed is not { } answer)
                return new HandoverDecision.Stop();

            // What arrived as several lines is never a choice here: a pasted /go would send on a
            // handover nobody has read. The whole block is dropped and the question asked again.
            if (answer.Lines > 1)
            {
                console.MarkupLine($"[yellow]That arrived as {answer.Lines} lines, so it was not used and was not sent to anyone. Type /go or /stop on its own, or a message for {author} on one line.[/]");
                continue;
            }

            var input = answer.Text;
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

    private void WriteRoles(ChatRoom room)
    {
        foreach (var participant in room.Participants)
        {
            var role = room.RoleOf(participant) is { } held ? $"{Markup.Escape(held.Name)}: {RelayConsole.Safe(held.Description)}" : "no role";
            console.MarkupLine($"[bold]{Markup.Escape(participant.DisplayName)}[/]  {role}");
        }

        console.MarkupLine($"[dim]Roles: {string.Join(", ", AgentRole.BuiltIn.Select(r => r.Name))}. Set one with /role <agent> <role>, your own with /role <agent> <name>: <description>, and remove it with /role <agent> none.[/]");
        console.MarkupLine("[dim]A role is what an agent is told to be. It does not limit what the agent can do.[/]");
    }

    // "<agent> <role>", "<agent> <name>: <description>" or "<agent> none".
    private void SetRole(ChatRoom room, string arguments)
    {
        var parts = arguments.Split([' ', '\t'], 2, StringSplitOptions.TrimEntries);
        if (room.FindParticipant(parts[0].TrimStart('@')) is not { } participant)
        {
            var known = string.Join(", ", room.Participants.Select(p => p.Id));
            console.MarkupLine($"[yellow]No agent named {Markup.Escape(parts[0])} in this room. Agents here: {Markup.Escape(known.Length > 0 ? known : "none")}.[/]");
            return;
        }

        var name = Markup.Escape(participant.DisplayName);
        var id = Markup.Escape(participant.Id);
        var spec = parts.Length > 1 ? parts[1] : string.Empty;
        var builtIn = string.Join(", ", AgentRole.BuiltIn.Select(r => r.Name));
        if (spec.Length == 0)
        {
            console.MarkupLine($"[yellow]Which role? /role {id} <role> with one of {builtIn}, or /role {id} <name>: <description> for your own.[/]");
            return;
        }

        if (spec.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            if (room.RoleOf(participant) is null)
            {
                console.MarkupLine($"[dim]{name} has no role.[/]");
                return;
            }

            room.SetRole(participant, null);
            var told = room.RoleWithdrawn(participant) ? ", and will be told so with the next message" : string.Empty;
            WriteMessage(room, room.AddMessage(ChatRoom.SystemId, SenderType.System, $"{participant.DisplayName} no longer has a role{told}."));
            return;
        }

        AgentRole? role;
        var replaces = string.Empty;
        if (spec.Split(':', 2, StringSplitOptions.TrimEntries) is [var custom, var description])
        {
            if (!AgentRole.TryCreate(custom, description, out role, out var error))
            {
                console.MarkupLine($"[yellow]{Markup.Escape(error)} {name}'s role is unchanged.[/]");
                return;
            }

            // In an exchange a role is listed beside the agent's @id, where an agent's name would read as an address.
            if (room.FindParticipant(role.Name) is not null)
            {
                console.MarkupLine($"[yellow]{role.Name} is the name of an agent in this room, so it cannot be the name of a role. {name}'s role is unchanged.[/]");
                return;
            }

            if (AgentRole.FindBuiltIn(role.Name) is not null)
                replaces = $" This replaces the built-in description of {role.Name} for {participant.DisplayName} only.";
        }
        else if ((role = AgentRole.FindBuiltIn(spec)) is null)
        {
            console.MarkupLine($"[yellow]No role named {Markup.Escape(spec)}. Use one of {builtIn}, or give your own a description: /role {id} <name>: <description>. {name}'s role is unchanged.[/]");
            return;
        }

        room.SetRole(participant, role);
        WriteMessage(room, room.AddMessage(ChatRoom.SystemId, SenderType.System, $"{participant.DisplayName} now has the role of {role.Name}: {role.Sentence}{replaces} A role is what an agent is told to be; it does not limit what the agent can do."));
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
        => e.Cancel = Interrupt();

    /// <returns>Whether a turn was cancelled. When none was, Relay is about to exit.</returns>
    internal bool Interrupt()
    {
        var cancelled = CancelTurn();
        if (!cancelled)
            RemoveApprovalFolder();
        return cancelled;
    }

    // Relay is being closed (terminal closed, kill, logoff). The agent CLI would otherwise outlive it, so
    // stop the request in flight and hold the shutdown until its process tree is gone. The signal is not
    // cancelled, so Relay still exits afterwards.
    internal void OnTerminating(PosixSignalContext context)
    {
        if (CancelTurn())
            _idle.Wait(ShutdownGrace);
        RemoveApprovalFolder();
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
        // Said without a terminal only when asked: there it is how Relay has always behaved.
        if (_desk is not null)
            console.MarkupLine($"[dim]{DescribeApprovals(room)}[/]");
        else if (_noApprovals != NoTerminal)
            console.MarkupLine($"[yellow]{DescribeApprovals(room)}[/]");
        if (HasAutoMode(room))
            console.MarkupLine($"[dim]{DescribeAuto(room)}{(_auto ? string.Empty : " Type /auto on to switch it on.")}[/]");
        if (_turnTimeout is not null)
            console.MarkupLine($"[dim]An agent whose turn runs longer than {Markup.Escape(_turnTimeout)} is stopped.[/]");
        // The default is in /help; a limit the user chose is said where they will see it at once.
        if (_maxTurnsChosen)
            console.MarkupLine($"[dim]An exchange between agents is at most {_maxTurns} agent turns, as you asked at start-up.[/]");
        console.MarkupLine($"[bold]# {Markup.Escape(room.Name)}[/]  [dim]/help for commands[/]");
        console.WriteLine();
    }

    private void WriteHelp(ChatRoom room)
    {
        console.MarkupLine("[bold]/help[/]      Show this help");
        console.MarkupLine("[bold]/agents[/]    Show the agents in this room");
        console.MarkupLine("[bold]/history[/]   Replay the room's message history");
        console.MarkupLine("[bold]/clear[/]     Clear the display (history is kept)");
        console.MarkupLine("[bold]/restart[/]   Start new agent conversations, keeping what was said in the room (/restart <agent> for one)");
        console.MarkupLine("[bold]/reset[/]     Start new agent conversations from a clean slate (/reset <agent> for one)");
        console.MarkupLine($"[bold]/pause[/]     Ask before one agent's message is passed to another: /pause on or /pause off (now {(_pause ? "on" : "off")})");
        console.MarkupLine($"[bold]/approvals[/] Be asked before a step an agent's own settings do not allow: /approvals on or /approvals off (now {(_desk is null ? "not available" : _desk.Enabled ? "on" : "off")})");
        console.MarkupLine($"[bold]/auto[/]      Let Claude decide the steps its settings do not, in its own auto mode: /auto on or /auto off (now {(_auto ? "on" : "off")})");
        console.MarkupLine("[bold]/role[/]      Show each agent's role; /role <agent> <role> gives one, /role <agent> <name>: <description> one of your own, /role <agent> none removes it");
        console.MarkupLine("[bold]/exit[/]      Leave the chatroom");
        foreach (var participant in room.Participants)
            console.MarkupLine($"[bold]@{Markup.Escape(participant.Id)} <message>[/]  Send a message to {Markup.Escape(participant.DisplayName)}");
        if (room.Participants.Where(p => p.Session is not null).ToList() is [var first, var second, ..])
            console.MarkupLine($"[bold]@{Markup.Escape(first.Id)} @{Markup.Escape(second.Id)} <message>[/]  Send it to {Markup.Escape(first.DisplayName)}, and let the agents named pass messages to each other. Only the first one is asked; the others speak if they are addressed.");
        console.MarkupLine($"[dim]An exchange between agents is at most {_maxTurns} agent turns for this session{(_maxTurnsChosen ? string.Empty : " (the default)")}. Only --max-turns at start-up changes it, from 1 to {ExchangeLimits.MaxAllowedTurns}.[/]");
        console.MarkupLine("[dim]Everything said in this room, by you or by an agent, is shown to each agent the next time you address it.[/]");
    }

    // What the agent is doing while Relay waits for its reply. Indented and dim, under the waiting
    // line that names the agent, so it cannot pass for a message in the room.
    private void WriteActivity(AgentActivity activity)
    {
        var text = RelayConsole.Step(activity, _workspace);
        lock (_writing)
            console.MarkupLine(activity.Kind == AgentActivityKind.Problem ? $"[yellow]  ! {text}[/]" : $"[dim]  - {text}[/]");
    }

    private void WriteMessage(ChatRoom room, ChatMessage message, bool includeHuman = false)
    {
        switch (message.SenderType)
        {
            case SenderType.Human when includeHuman:
                console.MarkupLine($"[bold green]You:[/] {ReplyRenderer.Message(message, console.Profile.Capabilities.Unicode)}");
                break;
            case SenderType.Agent:
                var name = room.FindParticipant(message.SenderId)?.DisplayName ?? message.SenderId;
                var to = message.TargetId is { } id && room.FindParticipant(id) is { } addressee ? $" to {addressee.DisplayName}" : string.Empty;
                console.MarkupLine($"[bold aqua]{Markup.Escape(name + to)}:[/] {ReplyRenderer.Message(message, console.Profile.Capabilities.Unicode)}");
                break;
            // Relay's own words, which may quote an agent's, so shown as written.
            case SenderType.System:
                console.MarkupLine($"[yellow]! {RelayConsole.Safe(message.Content)}[/]");
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
