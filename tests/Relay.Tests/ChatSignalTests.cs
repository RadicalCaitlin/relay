using System.Diagnostics;
using System.Text;

namespace Relay.Tests;

/// <summary>
/// Runs the real <c>relay start</c> against a fake <c>claude</c> on PATH that starts a long-lived
/// child, then signals Relay mid-request and checks nothing is left running.
/// </summary>
public sealed class ChatSignalTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("relay-signal-").FullName;
    private readonly StringBuilder _output = new();
    private readonly string _pidFile;
    private Process? _relay;

    public ChatSignalTests()
    {
        _pidFile = Path.Combine(_directory, "child.pid");
        if (OperatingSystem.IsWindows())
            return;

        var fakeClaude = Path.Combine(_directory, "claude");
        File.WriteAllText(fakeClaude, "#!/bin/sh\nsleep 300 &\necho $! > \"$RELAY_TEST_PIDFILE\"\nwait\n");
        File.SetUnixFileMode(fakeClaude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        if (_relay is { HasExited: false })
            _relay.Kill(entireProcessTree: true);
        _relay?.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [UnixFact]
    public async Task Interrupt_cancels_the_request_kills_the_agent_tree_and_keeps_the_chat_open()
    {
        var relay = StartRelay();
        await relay.StandardInput.WriteLineAsync("@claude hi");
        var child = await Poll.ForProcessIdAsync(_pidFile);

        Signal("INT", relay.Id);

        await Poll.UntilAsync(() => Output.Contains("was cancelled"), "the cancellation notice is shown");
        await Poll.UntilAsync(() => !Poll.IsRunning(child), "the agent's child process is gone");
        Assert.False(relay.HasExited);

        await relay.StandardInput.WriteLineAsync("/exit");
        await relay.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, relay.ExitCode);
    }

    [UnixFact]
    public async Task Termination_mid_request_kills_the_agent_tree_before_relay_exits()
    {
        var relay = StartRelay();
        await relay.StandardInput.WriteLineAsync("@claude hi");
        var child = await Poll.ForProcessIdAsync(_pidFile);

        Signal("TERM", relay.Id);

        await relay.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await Poll.UntilAsync(() => !Poll.IsRunning(child), "the agent's child process is gone");
    }

    private string Output
    {
        get { lock (_output) return _output.ToString(); }
    }

    private Process StartRelay()
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _directory,
        };
        startInfo.ArgumentList.Add(RelayAssembly());
        startInfo.ArgumentList.Add("start");
        startInfo.Environment["PATH"] = _directory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        startInfo.Environment["RELAY_TEST_PIDFILE"] = _pidFile;

        _relay = Process.Start(startInfo)!;
        _relay.StandardInput.AutoFlush = true;
        _relay.OutputDataReceived += (_, e) => Append(e.Data);
        _relay.ErrorDataReceived += (_, e) => Append(e.Data);
        _relay.BeginOutputReadLine();
        _relay.BeginErrorReadLine();
        return _relay;
    }

    private void Append(string? line)
    {
        lock (_output)
            _output.AppendLine(line);
    }

    private static void Signal(string name, int pid)
    {
        // The shell builtin, because minimal Linux images ship no standalone kill binary.
        using var kill = Process.Start("sh", ["-c", $"kill -{name} {pid}"]);
        kill.WaitForExit();
        Assert.Equal(0, kill.ExitCode);
    }

    // The CLI is built alongside the tests (see the project reference) but not copied next to them.
    private static string RelayAssembly()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = output.Parent!.Name;

        var root = output;
        while (!File.Exists(Path.Combine(root.FullName, "Relay.slnx")))
            root = root.Parent ?? throw new InvalidOperationException("Relay.slnx not found above the test output directory.");

        return Path.Combine(root.FullName, "src", "Relay.Cli", "bin", configuration, output.Name, "relay.dll");
    }
}
