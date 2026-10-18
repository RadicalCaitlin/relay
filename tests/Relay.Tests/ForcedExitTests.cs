using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Relay.Agents.Processes;
using Relay.Core.Agents;

namespace Relay.Tests;

/// <summary>
/// Kills Relay outright, as <c>taskkill /F</c>, <c>kill -9</c> or a crash would, so it can clean up
/// nothing, and checks the agents go with it: through the job object on Windows, and through
/// <see cref="AgentWatcher"/> elsewhere.
/// </summary>
public sealed class ForcedExitTests : IDisposable
{
    private readonly RelayHarness _harness = new();
    private readonly string _pidFile;

    public ForcedExitTests() => _pidFile = _harness.PathTo("child.pid");

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task The_agent_tree_does_not_outlive_relay_when_relay_is_killed()
    {
        _harness.InstallAgentScript(
            "claude",
            unixScript: "#!/bin/sh\nsleep 300 &\necho $! > \"$RELAY_TEST_DIR/child.pid\"\nwait\n",
            windowsScript: $"""
                @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -Command "$p = Start-Process ping -ArgumentList '-n 300 127.0.0.1' -PassThru -NoNewWindow; Set-Content -LiteralPath '{_pidFile}' -Value $p.Id; $p.WaitForExit()"

                """);
        var relay = _harness.Start("start");
        await relay.StandardInput.WriteLineAsync("@claude hi");
        var child = await Poll.ForProcessIdAsync(_pidFile);
        Assert.True(Poll.IsRunning(child));

        relay.Kill(entireProcessTree: false);

        // Not waiting for Relay itself: that waits for the end of its output, which a surviving child
        // would hold open.
        await AssertStopsAsync(child);
    }

    [UnixFact]
    public async Task The_agent_tree_does_not_outlive_relay_when_the_agent_exits_as_relay_goes()
    {
        // Like Node, it dies of the pipe Relay leaves broken, and its child is no longer in its tree.
        _harness.InstallAgentScript(
            "claude",
            unixScript: "#!/bin/sh\nsleep 300 &\necho $! > \"$RELAY_TEST_DIR/child.pid\"\nwhile echo . >&2; do sleep 0.1; done\n",
            windowsScript: "");
        var relay = _harness.Start("start");
        await relay.StandardInput.WriteLineAsync("@claude hi");
        var child = await Poll.ForProcessIdAsync(_pidFile);

        relay.Kill(entireProcessTree: false);

        await AssertStopsAsync(child);
    }

    [Fact]
    public async Task The_watcher_kills_the_tree_of_each_agent_still_running_when_relay_has_gone()
    {
        using var agent = StartWrapper();
        var child = await Poll.ForProcessIdAsync(_pidFile);

        await AgentWatcher.RunAsync(Input(AgentWatcher.Added(agent.Id, agent.StartTime)));

        await AssertStopsAsync(child);
        await Poll.UntilAsync(() => agent.HasExited, "the agent is gone", timeoutSeconds: 10);
    }

    [UnixFact]
    public async Task The_watcher_kills_what_an_agent_left_in_its_group_after_the_agent_has_exited()
    {
        var startInfo = RelayStartInfo(AgentWatcher.SessionCommand, "sh", "-c", "sleep 120 & echo $! > \"$0\"; read line", _pidFile);
        startInfo.RedirectStandardInput = true;
        using var agent = Process.Start(startInfo)!;
        var start = agent.StartTime;
        var child = await Poll.ForProcessIdAsync(_pidFile);
        agent.StandardInput.Close();
        await agent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Poll.IsRunning(child));

        await AgentWatcher.RunAsync(Input(AgentWatcher.Added(agent.Id, start)));

        await AssertStopsAsync(child);
    }

    [UnixFact]
    public async Task The_watcher_kills_an_agent_in_its_own_session_though_its_start_time_changed_when_it_became_the_agent()
    {
        // As on macOS, where the start time Relay sent is no longer the one the agent has.
        using var agent = Process.Start(RelayStartInfo(AgentWatcher.SessionCommand, "sh", "-c", "sleep 120 & echo $! > \"$0\"; wait", _pidFile))!;
        var child = await Poll.ForProcessIdAsync(_pidFile);

        await AgentWatcher.RunAsync(Input(AgentWatcher.Added(agent.Id, agent.StartTime - TimeSpan.FromMinutes(1))));

        await AssertStopsAsync(child);
        await Poll.UntilAsync(() => agent.HasExited, "the agent is gone", timeoutSeconds: 10);
    }

    [Fact]
    public async Task The_watcher_leaves_an_agent_that_relay_said_has_finished()
    {
        using var agent = StartWrapper();
        var child = await Poll.ForProcessIdAsync(_pidFile);
        try
        {
            await AgentWatcher.RunAsync(Input(AgentWatcher.Added(agent.Id, agent.StartTime), $"-{agent.Id}", $"+{agent.Id}", "+not-a-number", "-", "?1"));

            Assert.False(agent.HasExited);
            Assert.True(Poll.IsRunning(child));
        }
        finally
        {
            agent.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task The_watcher_leaves_a_process_that_took_an_agents_id_after_the_agent_exited()
    {
        using var agent = StartWrapper();
        var child = await Poll.ForProcessIdAsync(_pidFile);
        try
        {
            await AgentWatcher.RunAsync(Input(AgentWatcher.Added(agent.Id, agent.StartTime - TimeSpan.FromMinutes(1))));

            Assert.False(agent.HasExited);
            Assert.True(Poll.IsRunning(child));
        }
        finally
        {
            agent.Kill(entireProcessTree: true);
        }
    }

    [UnixFact]
    public async Task The_watcher_stops_the_agent_when_relay_has_gone_though_another_process_holds_its_input_open()
    {
        using var agent = StartWrapper();
        var child = await Poll.ForProcessIdAsync(_pidFile);
        var dotnet = new PathExecutableLocator().Find("dotnet")!;
        var relayDll = Path.Combine(AppContext.BaseDirectory, "relay.dll");
        // The sh stands in for Relay. The subshell feeding the watcher outlives it, as a process that
        // inherited Relay's end of the pipe would.
        using var relay = Process.Start("sh", ["-c", "{ echo \"$0\"; sleep 30; } | \"$1\" \"$2\" agent-watcher $$",
            AgentWatcher.Added(agent.Id, agent.StartTime), dotnet, relayDll])!;
        // Long enough for the runtime to start and the watcher to read the agent.
        await Task.Delay(TimeSpan.FromSeconds(3));

        relay.Kill(entireProcessTree: false);

        await AssertStopsAsync(child);
    }

    [UnixFact]
    public async Task The_watcher_stops_the_agent_when_relay_died_before_the_watcher_had_started()
    {
        using var agent = StartWrapper();
        var child = await Poll.ForProcessIdAsync(_pidFile);
        int gone;
        using (var exited = Process.Start("true")!)
        {
            await exited.WaitForExitAsync();
            gone = exited.Id;
        }

        // Its parent is not the Relay it was told of, as when that Relay died while it was starting.
        using var relay = Process.Start("sh", ["-c", "{ echo \"$0\"; sleep 30; } | \"$1\" \"$2\" agent-watcher \"$3\"",
            AgentWatcher.Added(agent.Id, agent.StartTime), new PathExecutableLocator().Find("dotnet")!,
            Path.Combine(AppContext.BaseDirectory, "relay.dll"), gone.ToString()])!;
        try
        {
            await AssertStopsAsync(child);
        }
        finally
        {
            relay.Kill(entireProcessTree: true);
        }
    }

    [UnixFact]
    public async Task Stopping_an_agent_also_stops_what_it_left_in_its_session()
    {
        // The subshell exits at once, so the sleep it started is no longer in the agent's tree.
        using var cancel = new CancellationTokenSource();
        var run = GuardedRunner().RunAsync(new ProcessRequest("sh", ["-c", "(sleep 120 & echo $! > \"$0\"); sleep 120", _pidFile]), cancel.Token);
        var child = await Poll.ForProcessIdAsync(_pidFile);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await AssertStopsAsync(child);
    }

    [UnixFact]
    public async Task An_agent_runs_in_a_session_of_its_own()
    {
        bool? leadsSession = null;
        // Asked while it is still running, after it has said who it is.
        await GuardedRunner().RunAsync(new ProcessRequest("sh", ["-c", "echo $$; sleep 1"])
        {
            OnOutputLine = line => leadsSession = getsid(int.Parse(line)) == int.Parse(line),
        });

        Assert.True(leadsSession);
    }

    [DllImport("libc")]
    private static extern int getsid(int pid);

    [UnixFact]
    public async Task An_agent_that_cannot_be_run_is_reported_with_the_reason()
    {
        var missing = _harness.PathTo("missing-agent");

        var ex = await Assert.ThrowsAsync<AgentException>(() => GuardedRunner().RunAsync(new ProcessRequest(missing, [])));

        Assert.Equal("Could not start 'missing-agent': No such file or directory", ex.Message);
        Assert.False(ex.PromptMayHaveBeenRead);
    }

    [UnixFact]
    public async Task A_watcher_that_dies_during_a_turn_is_replaced_without_waiting_for_another_agent()
    {
        var relay = FakeRelay();
        using var cancel = new CancellationTokenSource();
        var run = new ProcessRunner(new ProcessRequest("sh", [relay])).RunAsync(new ProcessRequest("sleep", ["120"]), cancel.Token);
        try
        {
            await Poll.UntilAsync(() => Logs(relay).Any(log => Poll.ReadShared(log).StartsWith('+')), "the first watcher is told about the agent");
            var watcherLog = Logs(relay).Single();
            var added = Poll.ReadShared(watcherLog);
            // Most likely within a second of its start, so the replacement waits until that second is up.
            await KillWatcherAsync(watcherLog);

            await Poll.UntilAsync(() => Logs(relay).Any(log => log != watcherLog && Poll.ReadShared(log) == added), "a new watcher is told about the agent", timeoutSeconds: 10);
        }
        finally
        {
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
    }

    [UnixFact]
    public async Task A_watcher_that_died_is_replaced_and_told_every_agent_still_running()
    {
        var relay = FakeRelay();
        var runner = new ProcessRunner(new ProcessRequest("sh", [relay]));
        using var cancel = new CancellationTokenSource();
        var first = runner.RunAsync(new ProcessRequest("sleep", ["120"]), cancel.Token);
        try
        {
            await Poll.UntilAsync(() => Logs(relay).Any(log => Poll.ReadShared(log).StartsWith('+')), "the first watcher is told about the agent");
            var watcherLog = Logs(relay).Single();
            var added = Poll.ReadShared(watcherLog);
            await KillWatcherAsync(watcherLog);

            // Long enough to be seen running, so it is added too. If the watcher died too soon to be
            // replaced at once, this is what notices.
            var second = await runner.RunAsync(new ProcessRequest("sleep", ["0.5"]));

            Assert.Equal(0, second.ExitCode);
            var replacement = Assert.Single(Logs(relay), log => log != watcherLog);
            Assert.Contains(added, File.ReadAllLines(replacement));
        }
        finally
        {
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        }
    }

    [UnixFact]
    public async Task The_watcher_outlasts_the_signals_a_terminal_sends_relay_and_stops_the_agent_when_relay_goes()
    {
        using var agent = StartWrapper();
        var child = await Poll.ForProcessIdAsync(_pidFile);
        var startInfo = RelayStartInfo(AgentWatcher.Command, Environment.ProcessId.ToString());
        startInfo.RedirectStandardInput = true;
        using var watcher = Process.Start(startInfo)!;
        await watcher.StandardInput.WriteLineAsync(AgentWatcher.Added(agent.Id, agent.StartTime));
        await watcher.StandardInput.FlushAsync();
        // Long enough for the runtime to start and the watcher to take the signals over.
        await Task.Delay(TimeSpan.FromSeconds(3));

        // The ones the terminal sends its whole foreground process group, Relay and watcher alike.
        foreach (var signal in (string[])["INT", "QUIT", "TSTP", "HUP", "TERM"])
        {
            using var kill = Process.Start("sh", ["-c", $"kill -{signal} {watcher.Id}"]);
            await kill.WaitForExitAsync();
        }
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(watcher.HasExited, "The watcher left on a signal meant for Relay.");
        Assert.True(Poll.IsRunning(child));

        watcher.StandardInput.Close();

        await AssertStopsAsync(child);
        await watcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    // Like the agent CLIs: a launcher that starts the real work as a child.
    private Process StartWrapper() => OperatingSystem.IsWindows()
        ? Process.Start("powershell", ["-NoProfile", "-NonInteractive", "-Command",
            $"$p = Start-Process ping -ArgumentList '-n 120 127.0.0.1' -PassThru -NoNewWindow; Set-Content -LiteralPath '{_pidFile}' -Value $p.Id; $p.WaitForExit()"])
        : Process.Start("sh", ["-c", "sleep 120 & echo $! > \"$0\"; wait", _pidFile]);

    private static ProcessRunner GuardedRunner()
        => new(new ProcessRequest(new PathExecutableLocator().Find("dotnet")!, [Path.Combine(AppContext.BaseDirectory, "relay.dll")]));

    /// <summary>Stands in for Relay: it runs the agent as it is, and its watcher records what it is told.</summary>
    private string FakeRelay()
    {
        var relay = _harness.PathTo("relay.sh");
        File.WriteAllText(relay, "case \"$1\" in\nagent-session) shift; exec \"$@\" ;;\nagent-watcher) exec cat >> \"$(dirname \"$0\")/watcher-$$.log\" ;;\nesac\n");
        return relay;
    }

    private static string[] Logs(string relay) => Directory.GetFiles(Path.GetDirectoryName(relay)!, "watcher-*.log");

    private static async Task KillWatcherAsync(string watcherLog)
    {
        using var watcher = Process.GetProcessById(int.Parse(Path.GetFileNameWithoutExtension(watcherLog)["watcher-".Length..]));
        watcher.Kill();
        await watcher.WaitForExitAsync();
    }

    private static ProcessStartInfo RelayStartInfo(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(new PathExecutableLocator().Find("dotnet")!) { UseShellExecute = false };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "relay.dll"));
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static MemoryStream Input(params string[] lines) => new(Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));

    private async Task AssertStopsAsync(int child)
    {
        try
        {
            await Poll.UntilAsync(() => !Poll.IsRunning(child), "the agent's child process is gone", timeoutSeconds: 10);
        }
        catch (Xunit.Sdk.XunitException) when (!OperatingSystem.IsWindows())
        {
            // What was left, with its parent and group, says which part of the cleanup failed.
            var trace = _harness.WatcherTrace;
            Assert.Fail($"The agent's child process {child} is still running.\n{await ProcessListAsync()}\n--- watcher trace:\n{(File.Exists(trace) ? Poll.ReadShared(trace) : "(none)")}");
        }
        finally
        {
            if (Poll.IsRunning(child))
                Process.GetProcessById(child).Kill();
        }
    }

    private static async Task<string> ProcessListAsync()
    {
        var startInfo = new ProcessStartInfo("ps", ["-A", "-o", "pid,ppid,pgid,stat,command"]) { RedirectStandardOutput = true, UseShellExecute = false };
        using var ps = Process.Start(startInfo)!;
        var lines = (await ps.StandardOutput.ReadToEndAsync()).Split('\n');
        return string.Join('\n', lines.Where((line, index) => index == 0 || line.Contains("relay") || line.Contains("sleep") || line.Contains("sh ")));
    }
}
