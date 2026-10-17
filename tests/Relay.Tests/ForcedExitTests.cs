using System.Diagnostics;

namespace Relay.Tests;

/// <summary>
/// Runs the real <c>relay start</c> against a fake <c>claude</c> that starts a long-lived child,
/// then kills Relay outright, as <c>taskkill /F</c> or a crash would, so it can clean up nothing.
/// </summary>
public sealed class ForcedExitTests : IDisposable
{
    private readonly RelayHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [WindowsFact("The job object that stops agents with Relay exists only on Windows.")]
    public async Task The_agent_tree_does_not_outlive_relay_when_relay_is_killed()
    {
        var pidFile = _harness.PathTo("child.pid");
        _harness.InstallAgentScript(
            "claude",
            unixScript: "#!/bin/sh\nexit 1\n",
            windowsScript: $"""
                @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -Command "$p = Start-Process ping -ArgumentList '-n 300 127.0.0.1' -PassThru -NoNewWindow; Set-Content -LiteralPath '{pidFile}' -Value $p.Id; $p.WaitForExit()"

                """);
        var relay = _harness.Start("start");
        await relay.StandardInput.WriteLineAsync("@claude hi");
        var child = await Poll.ForProcessIdAsync(pidFile);
        Assert.True(Poll.IsRunning(child));

        relay.Kill(entireProcessTree: false);

        // Not waiting for Relay itself: that waits for the end of its output, which a surviving child
        // would hold open.
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
