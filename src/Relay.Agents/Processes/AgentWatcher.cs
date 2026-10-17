using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Relay.Agents.Processes;

/// <summary>
/// What <c>relay agent-watcher</c> runs on macOS and Linux, which have no job object: a second
/// process that outlives a forced kill of Relay and then stops the agents Relay left behind. Relay
/// starts each agent in a process group of its own, through <c>relay agent-group</c>, and tells the
/// watcher each one it starts and each one that has finished, one line at a time on its standard
/// input. Only Relay holds the other end of that pipe, so the input ends exactly when Relay does,
/// however it ends, and every agent still on the list is killed with its whole group.
/// </summary>
public static class AgentWatcher
{
    public const string Command = "agent-watcher";

    public const string GroupCommand = "agent-group";

    /// <summary>What <c>relay agent-group</c> writes to standard error, before the reason, when it cannot run the agent.</summary>
    internal const string ExecFailed = "relay agent-group: could not run the agent: ";

    internal const int ExecFailedExitCode = 127;

    // A process started since the agent was added cannot be the agent, though its id may be the same.
    private static readonly TimeSpan SameStart = TimeSpan.FromSeconds(1);

    private const int SigKill = 9;
    private const int SigPipe = 13;

    public static async Task<int> RunAsync(Stream input)
    {
        // The terminal sends Ctrl+C, Ctrl+\, Ctrl+Z and its hangup to Relay and the watcher alike.
        // Relay decides what they mean; the watcher stays until Relay has gone.
        using var signals = OperatingSystem.IsWindows()
            ? null
            : new Signals([PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGTSTP, PosixSignal.SIGHUP, PosixSignal.SIGTERM]);

        var agents = new Dictionary<int, DateTime>();
        using var reader = new StreamReader(input);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith('+') && line[1..].Split(' ') is [var added, var ticks]
                && int.TryParse(added, CultureInfo.InvariantCulture, out var id)
                && long.TryParse(ticks, CultureInfo.InvariantCulture, out var start))
                agents[id] = new DateTime(start, DateTimeKind.Utc);
            else if (line.StartsWith('-') && int.TryParse(line.AsSpan(1), CultureInfo.InvariantCulture, out var removed))
                agents.Remove(removed);
        }

        foreach (var (id, start) in agents)
            Kill(id, start);
        return 0;
    }

    /// <summary>The line that tells the watcher about an agent: its id, and when it started.</summary>
    public static string Added(int id, DateTime start) => $"+{id} {start.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// What <c>relay agent-group</c> runs, in the process Relay started for an agent: it makes that
    /// process the leader of a new process group and then becomes the agent, keeping its id. Every
    /// process the agent starts is in that group, and stays in it when its parent exits, so the
    /// watcher can find them all by the agent's id.
    /// </summary>
    public static int RunInOwnGroup(string[] command)
    {
        // Relay's runtime ignores SIGPIPE, and an ignored signal stays ignored across exec. The agent
        // must have the default, as it would if Relay had started it directly.
        signal(SigPipe, IntPtr.Zero);
        setpgid(0, 0);
        execvp(command[0], [.. command, null]);

        Console.Error.WriteLine(ExecFailed + Marshal.GetLastPInvokeErrorMessage());
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
            kill(-id, SigKill);

        // Relay may be killed before the agent has made its group, and Windows has no groups at all.
        try
        {
            process?.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException)
        {
            // It exited after all, or could not be killed; nothing else can be done for it now.
        }
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
    /// The agent leads a process group of its own in Relay's session, which no process that took its
    /// id later does unless that terminal starts a job. Before it has made its group it is still the
    /// process Relay started, at the time Relay sent: macOS gives a process a new start time when it
    /// becomes the agent, so after that the time no longer matches there.
    /// </summary>
    private static bool IsAgent(Process process, DateTime start)
        => !OperatingSystem.IsWindows() && getpgid(process.Id) == process.Id && getsid(process.Id) == getsid(0)
            || StartedAt(process, start);

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
    private static extern int setpgid(int pid, int pgid);

    [DllImport("libc", SetLastError = true)]
    private static extern int getpgid(int pid);

    [DllImport("libc", SetLastError = true)]
    private static extern int getsid(int pid);

    [DllImport("libc", SetLastError = true)]
    private static extern int execvp(string file, string?[] argv);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

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
/// one. If the watcher has died, the next change starts another and tells it every agent still running.
/// </summary>
/// <param name="relay">How to start this same Relay; the watcher's command is added to its arguments.</param>
internal sealed class AgentWatch(ProcessRequest relay)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, DateTime> _agents = [];
    private Process? _process;

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

        if (_agents.Count == 0)
            return;

        // The first agent, or the watcher has died: either way this one must hear of every agent running.
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

        var process = Process.Start(startInfo)!;
        process.StandardInput.AutoFlush = true;
        return process;
    }
}
