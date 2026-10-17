using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Relay.Agents.Processes;

/// <summary>
/// What <c>relay agent-watcher</c> runs on macOS and Linux, which have no job object: a second
/// process that outlives a forced kill of Relay and then stops the agents Relay left behind. Relay
/// starts each agent in a session of its own, through <c>relay agent-session</c>, and tells the
/// watcher each one it starts and each one that has finished, one line at a time on its standard
/// input. Only Relay holds the other end of that pipe, so the input ends exactly when Relay does,
/// however it ends, and every agent still on the list is killed with its whole process group.
/// </summary>
public static class AgentWatcher
{
    public const string Command = "agent-watcher";

    public const string SessionCommand = "agent-session";

    /// <summary>What <c>relay agent-session</c> writes to standard error, before the reason, when it cannot run the agent.</summary>
    internal const string ExecFailed = "relay agent-session: could not run the agent: ";

    internal const int ExecFailedExitCode = 127;

    // A process started since the agent was added cannot be the agent, though its id may be the same.
    private static readonly TimeSpan SameStart = TimeSpan.FromSeconds(1);

    private const int SigPipe = 13;

    // Once Relay has gone, what it wrote before it went is still read, but no more than this is waited for.
    private static readonly TimeSpan Drain = TimeSpan.FromMilliseconds(200);

    /// <param name="relay">Relay's process id, or null when nothing but the input says Relay has gone.</param>
    public static async Task<int> RunAsync(Stream input, int? relay = null)
    {
        // The terminal sends Ctrl+C, Ctrl+\, Ctrl+Z and its hangup to Relay and the watcher alike.
        // Relay decides what they mean; the watcher stays until Relay has gone.
        using var signals = OperatingSystem.IsWindows()
            ? null
            : new Signals([PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGTSTP, PosixSignal.SIGHUP, PosixSignal.SIGTERM]);

        using var stop = new CancellationTokenSource();
        var orphaned = OrphanedAsync(relay, stop.Token);

        var agents = new Dictionary<int, DateTime>();
        using var reader = new StreamReader(input);
        while (true)
        {
            var read = reader.ReadLineAsync();
            if (!orphaned.IsCompleted)
                await Task.WhenAny(read, orphaned);
            if (!read.IsCompleted && await Task.WhenAny(read, Task.Delay(Drain)) != read)
                break;
            if (await read is not { } line)
                break;

            if (line.StartsWith('+') && line[1..].Split(' ') is [var added, var ticks]
                && int.TryParse(added, CultureInfo.InvariantCulture, out var id)
                && long.TryParse(ticks, CultureInfo.InvariantCulture, out var start))
                agents[id] = new DateTime(start, DateTimeKind.Utc);
            else if (line.StartsWith('-') && int.TryParse(line.AsSpan(1), CultureInfo.InvariantCulture, out var removed))
                agents.Remove(removed);
        }

        await stop.CancelAsync();
        foreach (var (id, start) in agents)
            Kill(id, start);
        return 0;
    }

    /// <summary>
    /// Ends once Relay is no longer the watcher's parent, which happens when Relay dies, even before
    /// the watcher has started. The input alone is not enough: macOS has no atomic way to make a pipe
    /// close on exec, so a process Relay starts at the same moment as the watcher can inherit Relay's
    /// end of the watcher's input and keep it open after Relay has gone.
    /// </summary>
    private static async Task OrphanedAsync(int? relay, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows() || relay is null)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            return;
        }

        while (!cancellationToken.IsCancellationRequested && getppid() == relay)
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>The line that tells the watcher about an agent: its id, and when it started.</summary>
    public static string Added(int id, DateTime start) => $"+{id} {start.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// What <c>relay agent-session</c> runs, in the process Relay started for an agent: it makes that
    /// process the leader of a new session and process group, and then becomes the agent, keeping its
    /// id. Every process the agent starts is in that group, and stays in it when its parent exits, so
    /// the watcher can find them all by the agent's id. The session has no terminal, so a tool that
    /// asks on the terminal (an ssh passphrase, sudo) fails at once, rather than being stopped for
    /// reading a terminal it does not own and holding the turn until its time limit.
    /// </summary>
    /// <param name="error">Where to say why the agent could not be run.</param>
    public static int RunInOwnSession(string[] command, Stream error)
    {
        // Relay's runtime ignores SIGPIPE, and an ignored signal stays ignored across exec. The agent
        // must have the default, as it would if Relay had started it directly.
        signal(SigPipe, IntPtr.Zero);
        setsid();
        execvp(command[0], [.. command, null]);

        // Read first: anything else the runtime calls may overwrite it.
        var reason = Marshal.GetLastPInvokeErrorMessage();
        using var writer = new StreamWriter(error);
        writer.WriteLine(ExecFailed + reason);
        return ExecFailedExitCode;
    }

    private static void Kill(int id, DateTime start)
    {
        using var process = Find(id);
        if (process is not null && !IsAgent(process, start))
            return;

        // While a process group exists its id is not given to a new process, so with no process of
        // that id left, any group of that id is the agent's: what it started and left behind.
        if (!OperatingSystem.IsWindows())
            ProcessKill.Group(id);

        // Relay may be killed before the agent has made its group, and Windows has no groups at all.
        if (process is not null)
            ProcessKill.Tree(process);
    }

    private static Process? Find(int id)
    {
        try
        {
            return Process.GetProcessById(id);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The agent leads a session of its own. Before it has made its session it is still the process
    /// Relay started, at the time Relay sent: macOS gives a process a new start time when it becomes
    /// the agent, so after that the time no longer matches there. A process that took the agent's id
    /// since is left alone unless it too leads a session, and Relay tells the watcher an agent has
    /// finished as soon as it has, so that id is on the list for only an instant.
    /// </summary>
    private static bool IsAgent(Process process, DateTime start)
    {
        if (OperatingSystem.IsWindows())
            return StartedAt(process, start);

        // macOS gives no session for a process that has exited but not yet been reaped. Its id is
        // still taken, so it was the agent or nothing.
        var session = getsid(process.Id);
        return session == process.Id || session == -1 || StartedAt(process, start);
    }

    private static bool StartedAt(Process process, DateTime start)
    {
        try
        {
            return (process.StartTime.ToUniversalTime() - start).Duration() < SameStart;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // It has just exited, so it was the agent or nothing: its id has not been given out again yet.
            return true;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr signal(int signal, IntPtr handler);

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    [DllImport("libc", SetLastError = true)]
    private static extern int getsid(int pid);

    [DllImport("libc")]
    private static extern int getppid();

    [DllImport("libc", SetLastError = true)]
    private static extern int execvp(string file, string?[] argv);

    private sealed class Signals(PosixSignal[] signals) : IDisposable
    {
        private readonly PosixSignalRegistration[] _registrations =
            [.. signals.Select(signal => PosixSignalRegistration.Create(signal, context => context.Cancel = true))];

        public void Dispose()
        {
            foreach (var registration in _registrations)
                registration.Dispose();
        }
    }
}

/// <summary>
/// Relay's end of the <see cref="AgentWatcher"/>: started with the first agent, and told about each
/// one. If the watcher dies, another is started at once and told every agent still running.
/// </summary>
/// <param name="relay">How to start this same Relay; the watcher's command is added to its arguments.</param>
internal sealed class AgentWatch(ProcessRequest relay)
{
    // A watcher that dies sooner than this is started again only once this much time has passed
    // since it started, so one that cannot run at all is not started over and over.
    private static readonly TimeSpan SteadyRun = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<int, DateTime> _agents = [];
    private Process? _process;
    private long _started;

    public ProcessRequest Relay => relay;

    /// <summary>
    /// Starts the watcher, if it is not running, before an agent is: telling it about the agent is
    /// then one write. Started only then, it can take long enough to start that Relay is killed
    /// after the agent has started and before the watcher has heard of it.
    /// </summary>
    /// <exception cref="IOException">The watcher could not be started, so no agent would be covered.</exception>
    public void EnsureRunning()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false })
                return;

            Discard();
            StartAndTellAll();
        }
    }

    /// <exception cref="IOException">The watcher could not be started or told, so the agent is not covered.</exception>
    public void Add(Process agent)
    {
        DateTime start;
        try
        {
            start = agent.StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            if (agent.HasExited)
                return;
            throw new IOException(ex.Message, ex);
        }

        lock (_gate)
        {
            _agents[agent.Id] = start;
            try
            {
                Tell(AgentWatcher.Added(agent.Id, start));
            }
            catch (IOException)
            {
                _agents.Remove(agent.Id);
                throw;
            }
        }
    }

    /// <summary>Only for an agent that has exited: one still running must stay on the list.</summary>
    public void Remove(int id)
    {
        lock (_gate)
        {
            if (!_agents.Remove(id))
                return;

            try
            {
                Tell($"-{id}");
            }
            catch (IOException)
            {
                // No watcher could be started for the agents still running. The next one tries again.
            }
        }
    }

    private void Tell(string line)
    {
        if (_process is not null)
        {
            try
            {
                _process.StandardInput.WriteLine(line);
                return;
            }
            catch (IOException)
            {
                Discard();
            }
        }

        if (_agents.Count > 0)
            StartAndTellAll();
    }

    private async Task ReplaceAsync(Process dead)
    {
        TimeSpan wait;
        lock (_gate)
        {
            if (_process != dead)
                return;

            Discard();
            wait = SteadyRun - Stopwatch.GetElapsedTime(_started);
        }

        if (wait > TimeSpan.Zero)
            await Task.Delay(wait);

        lock (_gate)
        {
            // An agent that started or finished meanwhile may have started one already.
            if (_process is not null || _agents.Count == 0)
                return;

            try
            {
                StartAndTellAll();
            }
            catch (IOException)
            {
                // The next agent to start or finish tries again.
            }
        }
    }

    /// <summary>For the first agent, or once the watcher has died: either way it must hear of every agent running.</summary>
    private void StartAndTellAll()
    {
        try
        {
            _process = Start();
            foreach (var (id, start) in _agents)
                _process.StandardInput.WriteLine(AgentWatcher.Added(id, start));
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            Discard();
            throw ex as IOException ?? new IOException(ex.Message, ex);
        }
    }

    private void Discard()
    {
        _process?.Dispose();
        _process = null;
    }

    private Process Start()
    {
        var startInfo = new ProcessStartInfo(relay.FileName)
        {
            RedirectStandardInput = true,
            // Not the terminal's or the caller's streams: whoever waits for the end of Relay's output
            // must not wait for the watcher too.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in relay.Arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add(AgentWatcher.Command);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        var process = Process.Start(startInfo)!;
        _started = Stopwatch.GetTimestamp();
        process.StandardInput.AutoFlush = true;
        process.Exited += (_, _) => _ = ReplaceAsync(process);
        process.EnableRaisingEvents = true;
        return process;
    }
}
