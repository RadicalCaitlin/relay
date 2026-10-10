using System.Diagnostics;
using System.Text;
using Relay.Agents.Processes;

namespace Relay.Tests;

/// <summary>
/// Runs the built <c>relay</c> CLI as a separate process in a scratch directory. Its PATH holds only
/// that directory and the operating system's own tools, so the only agents Relay can find are the
/// fakes a test installs, whether or not real ones are installed on the machine.
/// </summary>
internal sealed class RelayHarness : IDisposable
{
    public const string ClaudeSessionId = "ce6f4686-9f97-4a98-8fb4-3125a2b0e671";
    public const string CodexThreadId = "01a12603-b1c3-7d00-bf0c-d5720f7caecd";

    private readonly StringBuilder _output = new();
    private Process? _relay;

    public RelayHarness() => WorkingDirectory = Directory;

    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("relay-e2e-").FullName;

    /// <summary>Where Relay is started. Defaults to <see cref="Directory"/>.</summary>
    public string WorkingDirectory { get; set; }

    /// <summary>Everything the most recently started Relay process has printed.</summary>
    public string Output
    {
        get { lock (_output) return _output.ToString(); }
    }

    public string PathTo(string fileName) => Path.Combine(Directory, fileName);

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

    /// <summary>What <c>claude -p --output-format json</c> prints for a successful turn.</summary>
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
        => File.Exists(PathTo($"{name}-args.txt")) ? File.ReadAllLines(PathTo($"{name}-args.txt")).Select(l => l.Trim()).ToArray() : [];

    /// <summary>What the most recent turn wrote to the agent's standard input.</summary>
    public string StandardInput(string name) => File.ReadAllText(PathTo($"{name}-stdin.txt"));

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
        startInfo.Environment["PATH"] = Directory + Path.PathSeparator + SystemPath;
        startInfo.Environment["RELAY_TEST_DIR"] = Directory;
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
        foreach (var line in lines)
            await relay.StandardInput.WriteLineAsync(line);
        relay.StandardInput.Close();

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

    // Just enough for the fake agents' scripts (cat, sleep, findstr, ping), and nothing a real agent lives in.
    private static string SystemPath { get; } = OperatingSystem.IsWindows()
        ? Environment.SystemDirectory
        : string.Join(Path.PathSeparator, "/usr/bin", "/bin");

    private void Stop()
    {
        if (_relay is { HasExited: false })
            _relay.Kill(entireProcessTree: true);
        _relay?.Dispose();
        _relay = null;
    }

    private void Append(string? line)
    {
        lock (_output)
            _output.AppendLine(line);
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
