using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Relay.Agents.Processes;

/// <summary>
/// What <c>relay agent-watcher</c> runs on macOS and Linux, which have no job object: a second
/// process that outlives a forced kill of Relay and then stops the agents Relay left behind. Relay
/// tells it each agent it starts and each one that has finished, one line at a time on its standard
/// input. Only Relay holds the other end of that pipe, so the input ends exactly when Relay does,
/// however it ends, and every agent still on the list is killed with its whole tree.
/// </summary>
public static class AgentWatcher
{
    public const string Command = "agent-watcher";

    // A process started since the agent was added cannot be the agent, though its id may be the same.
    private static readonly TimeSpan SameStart = TimeSpan.FromSeconds(1);

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
            if (line.Length < 2 || !int.TryParse(line.AsSpan(1), out var id))
                continue;

            if (line[0] == '+' && StartOf(id) is { } start)
                agents[id] = start;
            else if (line[0] == '-')
                agents.Remove(id);
        }

        foreach (var (id, start) in agents)
            Kill(id, start);
        return 0;
    }

    private static DateTime? StartOf(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // It has already exited.
            return null;
        }
    }

    private static void Kill(int id, DateTime start)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            if ((process.StartTime.ToUniversalTime() - start).Duration() < SameStart)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or AggregateException)
        {
            // It exited after all, or could not be killed; nothing else can be done for it now.
        }
    }

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

/// <summary>Relay's end of the <see cref="AgentWatcher"/>: started with the first agent, and told about each one.</summary>
internal sealed class AgentWatch(ProcessRequest watcher)
{
    private readonly Lock _gate = new();
    private Process? _process;

    /// <exception cref="IOException">The watcher could not be started or told, so the agent is not covered.</exception>
    public void Add(int id) => Tell($"+{id}", start: true);

    public void Remove(int id)
    {
        try
        {
            Tell($"-{id}", start: false);
        }
        catch (IOException)
        {
            // The agent has gone already. The next one starts a new watcher.
        }
    }

    private void Tell(string line, bool start)
    {
        lock (_gate)
        {
            try
            {
                if (_process is null && start)
                    _process = Start();
                _process?.StandardInput.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
            {
                _process?.Dispose();
                _process = null;
                throw ex as IOException ?? new IOException(ex.Message, ex);
            }
        }
    }

    private Process Start()
    {
        var startInfo = new ProcessStartInfo(watcher.FileName)
        {
            RedirectStandardInput = true,
            // Not the terminal's or the caller's streams: whoever waits for the end of Relay's output
            // must not wait for the watcher too.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in watcher.Arguments)
            startInfo.ArgumentList.Add(argument);

        var process = Process.Start(startInfo)!;
        process.StandardInput.AutoFlush = true;
        return process;
    }
}
