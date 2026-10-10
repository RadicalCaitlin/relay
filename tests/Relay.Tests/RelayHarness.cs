using System.Diagnostics;
using System.Text;

namespace Relay.Tests;

/// <summary>
/// Runs the built <c>relay</c> CLI as a separate process in a scratch directory, with a fake
/// <c>claude</c> first on PATH so no real agent is ever involved.
/// </summary>
internal sealed class RelayHarness : IDisposable
{
    private readonly StringBuilder _output = new();
    private Process? _relay;

    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("relay-e2e-").FullName;

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

    /// <summary>Installs the fake agent. Each script can refer to the scratch directory as its own location.</summary>
    public void FakeClaude(string unixScript, string windowsScript)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(PathTo("claude.cmd"), windowsScript.ReplaceLineEndings("\r\n"));
            return;
        }

        File.WriteAllText(PathTo("claude"), unixScript.ReplaceLineEndings("\n"));
        File.SetUnixFileMode(PathTo("claude"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public Process Start(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            WorkingDirectory = Directory,
        };
        startInfo.ArgumentList.Add(RelayAssembly());
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["PATH"] = Directory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        startInfo.Environment["RELAY_TEST_DIR"] = Directory;
        // Keep the developer's real trusted folders out of the tests, and the tests out of theirs.
        startInfo.Environment["RELAY_HOME"] = PathTo("relay-home");

        _relay = Process.Start(startInfo)!;
        _relay.StandardInput.AutoFlush = true;
        _relay.OutputDataReceived += (_, e) => Append(e.Data);
        _relay.ErrorDataReceived += (_, e) => Append(e.Data);
        _relay.BeginOutputReadLine();
        _relay.BeginErrorReadLine();
        return _relay;
    }

    /// <summary>Sends the lines to a chat session, then waits for Relay to exit and returns everything it printed.</summary>
    public async Task<string> ChatAsync(string[] arguments, params string[] lines)
    {
        var relay = Start(arguments);
        foreach (var line in lines)
            await relay.StandardInput.WriteLineAsync(line);
        relay.StandardInput.Close();

        await relay.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(0, relay.ExitCode);
        return Output;
    }

    public void Dispose()
    {
        if (_relay is { HasExited: false })
            _relay.Kill(entireProcessTree: true);
        _relay?.Dispose();
        System.IO.Directory.Delete(Directory, recursive: true);
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
