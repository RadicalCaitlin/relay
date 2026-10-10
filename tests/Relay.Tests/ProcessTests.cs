using System.Diagnostics;
using Relay.Agents.Processes;
using Relay.Core.Agents;

namespace Relay.Tests;

public class ProcessTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("relay-tests-").FullName;
    private readonly ProcessRunner _runner = new();

    private static ProcessRequest Sleep => OperatingSystem.IsWindows()
        ? new("ping", ["-n", "60", "127.0.0.1"])
        : new("sleep", ["60"]);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Runner_captures_output_and_exit_code()
    {
        var result = await _runner.RunAsync(new("dotnet", ["--version"]));

        Assert.Equal(0, result.ExitCode);
        Assert.Matches(@"^\d+\.\d+", result.StandardOutput);
    }

    [Fact]
    public async Task Runner_reports_a_nonzero_exit_code_instead_of_throwing()
    {
        var result = await _runner.RunAsync(new("dotnet", ["relay-no-such-command"]));

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Runner_copes_with_a_process_that_exits_without_reading_its_input()
    {
        // Far more than a pipe holds, so the write is still under way when the process has gone.
        var result = await _runner.RunAsync(new("dotnet", ["--version"]) { StandardInput = new string('x', 4 * 1024 * 1024) });

        Assert.Equal(0, result.ExitCode);
        Assert.Matches(@"^\d+\.\d+", result.StandardOutput);
    }

    [Fact]
    public async Task Runner_reports_an_executable_that_cannot_be_started()
        => await Assert.ThrowsAsync<AgentException>(() => _runner.RunAsync(new(Path.Combine(_directory, "missing"), [])));

    [Fact]
    public async Task Runner_kills_the_process_when_cancelled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _runner.RunAsync(Sleep, cts.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Runner_kills_descendants_of_the_process_when_cancelled()
    {
        // A wrapper that starts the real work as a child, like the cmd/node/sh shims the agent CLIs install.
        var pidFile = Path.Combine(_directory, "child.pid");
        var wrapper = OperatingSystem.IsWindows()
            ? new ProcessRequest("powershell", ["-NoProfile", "-NonInteractive", "-Command",
                $"$p = Start-Process ping -ArgumentList '-n 120 127.0.0.1' -PassThru -NoNewWindow; Set-Content -LiteralPath '{pidFile}' -Value $p.Id; $p.WaitForExit()"])
            : new ProcessRequest("sh", ["-c", "sleep 120 & echo $! > \"$0\"; wait", pidFile]);
        using var cts = new CancellationTokenSource();

        var run = _runner.RunAsync(wrapper, cts.Token);
        var child = await Poll.ForProcessIdAsync(pidFile);
        Assert.True(Poll.IsRunning(child));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await Poll.UntilAsync(() => !Poll.IsRunning(child), "the descendant process is gone", timeoutSeconds: 10);
    }

    [Fact]
    public async Task Runner_kills_the_process_and_reports_a_timeout()
    {
        var ex = await Assert.ThrowsAsync<AgentException>(
            () => _runner.RunAsync(Sleep with { Timeout = TimeSpan.FromMilliseconds(300) }));

        Assert.Contains("did not finish", ex.Message);
    }

    [Fact]
    public async Task Runner_does_not_wait_for_a_background_process_that_inherited_the_pipes()
    {
        var request = OperatingSystem.IsWindows()
            ? new ProcessRequest("cmd", ["/c", "echo done & start /b ping -n 15 127.0.0.1"])
            : new ProcessRequest("sh", ["-c", "echo done; sleep 15 &"]);
        var stopwatch = Stopwatch.StartNew();

        var result = await _runner.RunAsync(request);

        Assert.StartsWith("done", result.StandardOutput);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void Locator_finds_an_executable_in_a_path_directory()
    {
        var file = Path.Combine(_directory, OperatingSystem.IsWindows() ? "relaytool.cmd" : "relaytool");
        File.WriteAllText(file, string.Empty);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var path = string.Join(Path.PathSeparator, Path.Combine(_directory, "nowhere"), _directory);
        var found = PathExecutableLocator.Find("relaytool", path, ".EXE;.CMD");

        Assert.Equal(file, found, ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void Locator_skips_files_the_os_would_not_execute()
    {
        // Windows: an extensionless shell script, as npm installs next to its .cmd shim. Unix: no execute bit.
        File.WriteAllText(Path.Combine(_directory, "relaytool"), string.Empty);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path.Combine(_directory, "relaytool"), UnixFileMode.UserRead);

        Assert.Null(PathExecutableLocator.Find("relaytool", _directory, ".EXE;.CMD"));
    }

    [Fact]
    public void Locator_ignores_relative_path_entries_so_the_working_directory_cannot_supply_the_agent()
    {
        // Under the current directory, so that a relative entry can reach it on any drive layout.
        var relative = $"relay-locator-{Guid.NewGuid():N}";
        var directory = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, relative)).FullName;
        try
        {
            var file = Path.Combine(directory, OperatingSystem.IsWindows() ? "relaytool.cmd" : "relaytool");
            File.WriteAllText(file, string.Empty);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.Copy(file, Path.Combine(Environment.CurrentDirectory, Path.GetFileName(file)), overwrite: true);

            Assert.Null(PathExecutableLocator.Find("relaytool", string.Join(Path.PathSeparator, ".", relative), ".EXE;.CMD"));
            Assert.NotNull(PathExecutableLocator.Find("relaytool", directory, ".EXE;.CMD"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            File.Delete(Path.Combine(Environment.CurrentDirectory, OperatingSystem.IsWindows() ? "relaytool.cmd" : "relaytool"));
        }
    }

    [Fact]
    public async Task Runner_does_not_let_a_cmd_shim_pick_up_a_program_planted_in_the_working_directory()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Mirrors the npm shims: claude.cmd calls "node" by bare name, and cmd.exe would look in the
        // working directory (here an untrusted repository) before PATH.
        var shimDirectory = Directory.CreateDirectory(Path.Combine(_directory, "shim")).FullName;
        var repository = Directory.CreateDirectory(Path.Combine(_directory, "repo")).FullName;
        File.WriteAllText(Path.Combine(shimDirectory, "agent.cmd"), "@relayhelper\r\n");
        File.WriteAllText(Path.Combine(repository, "relayhelper.cmd"), "@echo planted\r\n");

        // Some terminals already set the variable the runner relies on; clear it so the runner is what protects us.
        const string variable = "NoDefaultCurrentDirectoryInExePath";
        var inherited = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, null);
        try
        {
            var result = await _runner.RunAsync(new(Path.Combine(shimDirectory, "agent.cmd"), []) { WorkingDirectory = repository });

            Assert.DoesNotContain("planted", result.StandardOutput);
            Assert.NotEqual(0, result.ExitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, inherited);
        }
    }

    [Fact]
    public void Search_path_keeps_only_absolute_entries()
    {
        var absolute = _directory;
        var path = string.Join(Path.PathSeparator, ".", absolute, "", "relative" + Path.DirectorySeparatorChar + "bin", $"\"{absolute}\"");

        Assert.Equal(string.Join(Path.PathSeparator, absolute, $"\"{absolute}\""), SearchPath.WithoutRelativeEntries(path));
        Assert.Equal(string.Empty, SearchPath.WithoutRelativeEntries(null));
    }

    [Fact]
    public async Task Runner_does_not_pass_relative_path_entries_on_to_the_agent()
    {
        // The shim calls a helper by bare name, as the agent CLIs call "node". With "." on PATH the
        // helper planted in the working directory (an untrusted repository) would be found.
        var shimDirectory = Directory.CreateDirectory(Path.Combine(_directory, "shim")).FullName;
        var repository = Directory.CreateDirectory(Path.Combine(_directory, "repo")).FullName;
        string shim;
        if (OperatingSystem.IsWindows())
        {
            shim = Path.Combine(shimDirectory, "agent.cmd");
            File.WriteAllText(shim, "@relaydothelper\r\n");
            File.WriteAllText(Path.Combine(repository, "relaydothelper.cmd"), "@echo planted\r\n");
        }
        else
        {
            shim = Path.Combine(shimDirectory, "agent");
            var planted = Path.Combine(repository, "relaydothelper");
            File.WriteAllText(shim, "#!/bin/sh\nrelaydothelper\n");
            File.WriteAllText(planted, "#!/bin/sh\necho planted\n");
            File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.SetUnixFileMode(planted, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        var inherited = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", inherited + Path.PathSeparator + ".");
        try
        {
            var result = await _runner.RunAsync(new(shim, []) { WorkingDirectory = repository });

            Assert.DoesNotContain("planted", result.StandardOutput);
            Assert.NotEqual(0, result.ExitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", inherited);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Locator_returns_null_without_a_path(string? path)
        => Assert.Null(PathExecutableLocator.Find("relaytool", path, null));
}
