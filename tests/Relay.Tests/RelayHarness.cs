using System.Diagnostics;
using System.Text;
using Relay.Agents.Processes;

namespace Relay.Tests;

/// <summary>
/// Runs the built <c>relay</c> CLI as a separate process in a scratch directory. Its PATH holds only
/// that directory and the few tools the fake agents' scripts call, so the only agents Relay can find
/// are the fakes a test installs, whether or not real ones are installed on the machine.
/// </summary>
internal sealed class RelayHarness : IDisposable
{
    public const string ClaudeSessionId = "ce6f4686-9f97-4a98-8fb4-3125a2b0e671";
    public const string CodexThreadId = "01a12603-b1c3-7d00-bf0c-d5720f7caecd";

    private readonly StringBuilder _output = new();
    private readonly string _toolPath;
    private Process? _relay;

    public RelayHarness()
    {
        WorkingDirectory = Directory;
        _toolPath = CreateToolPath();
    }

    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("relay-e2e-").FullName;

    /// <summary>Where Relay is started. Defaults to <see cref="Directory"/>.</summary>
    public string WorkingDirectory { get; set; }

    /// <summary>Everything the most recently started Relay process has printed.</summary>
    public string Output
    {
        get { lock (_output) return _output.ToString(); }
    }

    public string PathTo(string fileName) => Path.Combine(Directory, fileName);

    // TEMPORARY DIAGNOSTIC: outside Directory, so a watcher still writing it cannot stop Dispose deleting that.
    public string WatcherTrace => Path.Combine(Path.GetTempPath(), Path.GetFileName(Directory) + ".watcher.trace");

    /// <summary>
    /// Removes all whitespace. Relay wraps output at the console width, so a long line (anything
    /// containing the scratch path) can be split anywhere; compare squashed text to ignore that.
    /// </summary>
    public static string Squash(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    // xUnit truncates long strings in its own messages, so these print everything Relay wrote.
    public static void AssertShown(string expected, string output)
        => Assert.True(Squash(output).Contains(Squash(expected)), $"Expected \"{expected}\" in:\n{output}");

    public static void AssertNotShown(string unexpected, string output)
        => Assert.False(Squash(output).Contains(Squash(unexpected)), $"Did not expect \"{unexpected}\" in:\n{output}");

    /// <summary>The last line <c>claude -p --output-format stream-json</c> prints for a successful turn: the result.</summary>
    /// <param name="jsonEscapedText">Written with JSON escapes, so the file on disk stays plain ASCII.</param>
    public static string ClaudeReply(string jsonEscapedText)
        => $$"""{"type":"result","is_error":false,"result":"{{jsonEscapedText}}","session_id":"{{ClaudeSessionId}}"}""";

    /// <summary>What <c>codex exec --json</c> prints for a successful turn.</summary>
    public static string CodexReply(string jsonEscapedText)
        => $$$"""
            {"type":"thread.started","thread_id":"{{{CodexThreadId}}}"}
            {"type":"turn.started"}
            {"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"{{{jsonEscapedText}}}"}}
            {"type":"turn.completed","usage":{"input_tokens":1}}
            """;

    /// <summary>
    /// Installs a fake agent CLI that answers <c>--version</c>, records each invocation's arguments
    /// and standard input, then prints the given output and exits with the given code.
    /// </summary>
    public void InstallAgent(string name, string stdout, string stderr = "", int exitCode = 0)
    {
        File.WriteAllText(PathTo($"{name}-version.txt"), $"9.9.9 (fake {name})\n");
        SetAgentOutput(name, stdout, stderr, exitCode);
        InstallAgentScript(
            name,
            unixScript: $"""
                #!/bin/sh
                d="$RELAY_TEST_DIR"
                if [ "$1" = "--version" ]; then cat "$d/{name}-version.txt"; exit 0; fi
                echo "$*" >> "$d/{name}-args.txt"
                cat > "$d/{name}-stdin.txt"
                cat "$d/{name}-stdout.txt"
                cat "$d/{name}-stderr.txt" >&2
                exit "$(cat "$d/{name}-exit.txt")"

                """,
            windowsScript: $"""
                @echo off
                set "d=%RELAY_TEST_DIR%"
                if "%~1"=="--version" (type "%d%\{name}-version.txt" & exit /b 0)
                >>"%d%\{name}-args.txt" echo %*
                findstr "^" > "%d%\{name}-stdin.txt"
                type "%d%\{name}-stdout.txt"
                type "%d%\{name}-stderr.txt" 1>&2
                set /p code=<"%d%\{name}-exit.txt"
                exit /b %code%

                """);
    }

    /// <summary>Changes what an agent installed with <see cref="InstallAgent"/> prints from now on.</summary>
    public void SetAgentOutput(string name, string stdout, string stderr = "", int exitCode = 0)
    {
        File.WriteAllText(PathTo($"{name}-stdout.txt"), stdout);
        File.WriteAllText(PathTo($"{name}-stderr.txt"), stderr);
        File.WriteAllText(PathTo($"{name}-exit.txt"), $"{exitCode}\n");
    }

    /// <summary>The arguments of each turn sent to an agent installed with <see cref="InstallAgent"/>, oldest first.</summary>
    public IReadOnlyList<string> Invocations(string name)
        => Poll.ReadShared(PathTo($"{name}-args.txt")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>What the most recent turn wrote to the agent's standard input.</summary>
    public string StandardInput(string name) => Poll.ReadShared(PathTo($"{name}-stdin.txt"));

    /// <summary>Installs a fake agent CLI with its own behaviour.</summary>
    public void InstallAgentScript(string name, string unixScript, string windowsScript)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(PathTo($"{name}.cmd"), windowsScript.ReplaceLineEndings("\r\n"));
            return;
        }

        File.WriteAllText(PathTo(name), unixScript.ReplaceLineEndings("\n"));
        File.SetUnixFileMode(PathTo(name), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public Process Start(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(DotNet)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            WorkingDirectory = WorkingDirectory,
        };
        startInfo.ArgumentList.Add(RelayAssembly());
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["PATH"] = Directory + Path.PathSeparator + _toolPath;
        startInfo.Environment["RELAY_TEST_DIR"] = Directory;
        // TEMPORARY DIAGNOSTIC for the intermittent macOS failure; removed once it is understood.
        startInfo.Environment["RELAY_WATCHER_TRACE"] = WatcherTrace;
        // Keep the developer's real trusted folders out of the tests, and the tests out of theirs.
        startInfo.Environment["RELAY_HOME"] = PathTo("relay-home");
        // Spectre.Console turns colour escape codes on when it sees GitHub Actions, even with output
        // redirected. The tests assert on plain text, and on the absence of escape codes from agents.
        startInfo.Environment.Remove("GITHUB_ACTIONS");
        startInfo.Environment["NO_COLOR"] = "1";

        Stop();
        lock (_output)
            _output.Clear();

        _relay = Process.Start(startInfo)!;
        _relay.StandardInput.AutoFlush = true;
        _relay.OutputDataReceived += (_, e) => Append(e.Data);
        _relay.ErrorDataReceived += (_, e) => Append(e.Data);
        _relay.BeginOutputReadLine();
        _relay.BeginErrorReadLine();
        return _relay;
    }

    /// <summary>Feeds the lines to Relay, waits for it to exit, and returns its exit code and what that run printed.</summary>
    public async Task<(int ExitCode, string Output)> RunAsync(string[] arguments, params string[] lines)
    {
        var relay = Start(arguments);
        try
        {
            foreach (var line in lines)
                await relay.StandardInput.WriteLineAsync(line);
            relay.StandardInput.Close();
        }
        catch (IOException)
        {
            // Relay has stopped reading: it left on an earlier line, which some tests intend, and
            // the pipe closed before the rest was written. If it has not exited, the wait below fails.
        }

        await relay.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        return (relay.ExitCode, Output);
    }

    /// <summary>As <see cref="RunAsync"/>, for a run that is expected to succeed.</summary>
    public async Task<string> ChatAsync(string[] arguments, params string[] lines)
    {
        var (exitCode, output) = await RunAsync(arguments, lines);
        Assert.True(exitCode == 0, $"relay {string.Join(' ', arguments)} exited with code {exitCode}:\n{output}");
        return output;
    }

    public void Dispose()
    {
        Stop();
        System.IO.Directory.Delete(Directory, recursive: true);
    }

    // Resolved once from the real PATH, because the PATH given to Relay no longer contains it.
    private static string DotNet { get; } = new PathExecutableLocator().Find("dotnet")
        ?? throw new InvalidOperationException("dotnet was not found on PATH.");

    // The tools the fake agents' scripts call by name, and nothing else. On Unix that cannot be /usr/bin
    // or /bin themselves, because a real agent can be installed there, so the tools are linked into a
    // folder of our own. The scripts start with an absolute #!/bin/sh, so the shell needs no entry.
    // On Windows the scripts need findstr from System32, where no agent CLI is ever installed.
    private string CreateToolPath()
    {
        if (OperatingSystem.IsWindows())
            return Environment.SystemDirectory;

        var tools = System.IO.Directory.CreateDirectory(PathTo("tools")).FullName;
        foreach (var tool in (string[])["cat", "sleep"])
        {
            var target = PathExecutableLocator.Find(tool, "/usr/bin:/bin", pathExt: null)
                ?? throw new InvalidOperationException($"{tool} was not found in /usr/bin or /bin.");
            File.CreateSymbolicLink(Path.Combine(tools, tool), target);
        }

        return tools;
    }

    private void Stop()
    {
        if (_relay is { HasExited: false })
        {
            _relay.Kill(entireProcessTree: true);
            // Killing does not wait, and on Windows a process that is still going holds its working
            // directory, which is the folder about to be deleted.
            _relay.WaitForExit(TimeSpan.FromSeconds(30));
        }
        _relay?.Dispose();
        _relay = null;
    }

    private void Append(string? line)
    {
        lock (_output)
            _output.AppendLine(line);
    }

    // The copy the project reference puts next to the tests, so the tests that launch the CLI and
    // the tests that call into it run the same build.
    private static string RelayAssembly() => Path.Combine(AppContext.BaseDirectory, "relay.dll");
}
