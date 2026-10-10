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

    [UnixFact]
    public async Task Interrupt_at_a_pause_cancels_the_exchange_and_the_next_line_typed_is_still_read()
    {
        var relay = await StartAndWaitForPauseAsync();

        Signal("INT", relay.Id);

        await Poll.UntilAsync(() => _harness.Output.Contains("was cancelled before"), "the cancellation notice is shown");
        Assert.False(relay.HasExited);

        // The read the interrupt abandoned is still the one that receives this line.
        await relay.StandardInput.WriteLineAsync("/pause");
        await Poll.UntilAsync(() => _harness.Output.Contains("Pause mode is on. Use"), "the line typed after the interrupt is read");
        await relay.StandardInput.WriteLineAsync("/exit");
        await relay.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, relay.ExitCode);
        Assert.Empty(_harness.Invocations("codex"));
    }

    [UnixFact]
    public async Task Termination_at_a_pause_does_not_wait_for_an_agent_that_is_not_running()
    {
        var relay = await StartAndWaitForPauseAsync();

        var stopwatch = Stopwatch.StartNew();
        Signal("TERM", relay.Id);
        await relay.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        // Waiting out the shutdown grace period would take four seconds.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Relay took {stopwatch.Elapsed} to exit.");
    }

    private async Task<Process> StartAndWaitForPauseAsync()
    {
        _harness.InstallAgent("claude", RelayHarness.ClaudeReply(@"@codex\nyour turn"));
        _harness.InstallAgent("codex", RelayHarness.CodexReply("unused"));
        var relay = _harness.Start("start");
        await relay.StandardInput.WriteLineAsync("/pause on");
        await relay.StandardInput.WriteLineAsync("@claude @codex agree");
        await Poll.UntilAsync(() => _harness.Output.Contains("Paused."), "the handover is held");
        return relay;
    }

    private static void Signal(string name, int pid)
    {
        // The shell builtin, because minimal Linux images ship no standalone kill binary.
        using var kill = Process.Start("sh", ["-c", $"kill -{name} {pid}"]);
        kill.WaitForExit();
        Assert.Equal(0, kill.ExitCode);
    }
}
