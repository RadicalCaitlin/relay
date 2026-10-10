using System.Diagnostics;

namespace Relay.Tests;

/// <summary>
/// Runs the real <c>relay start</c> against a fake <c>claude</c> that starts a long-lived child,
/// then signals Relay mid-request and checks nothing is left running.
/// </summary>
public sealed class ChatSignalTests : IDisposable
{
    private readonly RelayHarness _harness = new();
    private readonly string _pidFile;

    public ChatSignalTests()
    {
        _pidFile = _harness.PathTo("child.pid");
        _harness.InstallAgentScript(
            "claude",
            unixScript: "#!/bin/sh\nsleep 300 &\necho $! > \"$RELAY_TEST_DIR/child.pid\"\nwait\n",
            windowsScript: "@exit /b 1\n");
    }

    public void Dispose() => _harness.Dispose();

    [UnixFact]
    public async Task Interrupt_cancels_the_request_kills_the_agent_tree_and_keeps_the_chat_open()
    {
        var relay = _harness.Start("start");
        await relay.StandardInput.WriteLineAsync("@claude hi");
        var child = await Poll.ForProcessIdAsync(_pidFile);

        Signal("INT", relay.Id);

        await Poll.UntilAsync(() => _harness.Output.Contains("was cancelled"), "the cancellation notice is shown");
        await Poll.UntilAsync(() => !Poll.IsRunning(child), "the agent's child process is gone");
        Assert.False(relay.HasExited);

        await relay.StandardInput.WriteLineAsync("/exit");
        await relay.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, relay.ExitCode);
    }

    [UnixFact]
    public async Task Termination_mid_request_kills_the_agent_tree_before_relay_exits()
    {
        var relay = _harness.Start("start");
        await relay.StandardInput.WriteLineAsync("@claude hi");
        var child = await Poll.ForProcessIdAsync(_pidFile);

        Signal("TERM", relay.Id);

        await relay.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await Poll.UntilAsync(() => !Poll.IsRunning(child), "the agent's child process is gone");
    }

    private static void Signal(string name, int pid)
    {
        // The shell builtin, because minimal Linux images ship no standalone kill binary.
        using var kill = Process.Start("sh", ["-c", $"kill -{name} {pid}"]);
        kill.WaitForExit();
        Assert.Equal(0, kill.ExitCode);
    }
}
