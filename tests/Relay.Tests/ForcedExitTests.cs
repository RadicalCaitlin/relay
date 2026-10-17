using System.Diagnostics;
using System.Text;
using Relay.Agents.Processes;

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

    [Fact]
    public async Task The_watcher_kills_the_tree_of_each_agent_still_running_when_relay_has_gone()
    {
        using var agent = StartWrapper();
        var child = await Poll.ForProcessIdAsync(_pidFile);

        await AgentWatcher.RunAsync(Input($"+{agent.Id}"));

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
            await AgentWatcher.RunAsync(Input($"+{agent.Id}", $"-{agent.Id}", "+not-a-number", "-", "?1"));

            Assert.False(agent.HasExited);
            Assert.True(Poll.IsRunning(child));
        }
        finally
        {
            agent.Kill(entireProcessTree: true);
        }
    }

    [UnixFact]
    public async Task The_watcher_outlasts_the_signals_a_terminal_sends_relay_and_stops_the_agent_when_relay_goes()
    {
        using var agent = StartWrapper();
        var child = await Poll.ForProcessIdAsync(_pidFile);
        var startInfo = new ProcessStartInfo(new PathExecutableLocator().Find("dotnet")!) { RedirectStandardInput = true, UseShellExecute = false };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "relay.dll"));
        startInfo.ArgumentList.Add(AgentWatcher.Command);
        using var watcher = Process.Start(startInfo)!;
        await watcher.StandardInput.WriteLineAsync($"+{agent.Id}");
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

    private static MemoryStream Input(params string[] lines) => new(Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));

    private static async Task AssertStopsAsync(int child)
    {
        try
        {
            await Poll.UntilAsync(() => !Poll.IsRunning(child), "the agent's child process is gone", timeoutSeconds: 10);
        }
        finally
        {
            if (Poll.IsRunning(child))
                Process.GetProcessById(child).Kill();
        }
    }
}
