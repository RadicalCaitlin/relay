using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Relay.Core.Agents;

namespace Relay.Agents.Processes;

public sealed record ProcessRequest(string FileName, IReadOnlyList<string> Arguments)
{
    public string? StandardInput { get; init; }

    public string? WorkingDirectory { get; init; }

    public TimeSpan? Timeout { get; init; }
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IProcessRunner
{
    /// <exception cref="AgentException">The process could not be started or timed out.</exception>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(5);

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(request.FileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
        };
        foreach (var argument in request.Arguments)
            startInfo.ArgumentList.Add(argument);

        // The agent CLIs are shims that call "node" by bare name, so a node planted in the working
        // directory must not be found: neither through a relative PATH entry, nor through cmd.exe's
        // habit of looking in the current directory before PATH.
        startInfo.Environment["PATH"] = SearchPath.WithoutRelativeEntries(Environment.GetEnvironmentVariable("PATH"));
        if (OperatingSystem.IsWindows())
            startInfo.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";

        var name = Path.GetFileNameWithoutExtension(request.FileName);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new AgentException($"Could not start '{name}': {ex.Message}", ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout is { } limit)
            timeout.CancelAfter(limit);

        var output = new StringBuilder();
        var error = new StringBuilder();
        var pumps = Task.WhenAll(PumpAsync(process.StandardOutput, output), PumpAsync(process.StandardError, error));
        try
        {
            await WriteInputAsync(process, request.StandardInput, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            // A descendant that inherited the pipes (a dev server the agent started, say) can hold them
            // open long after the CLI exits, so stop waiting for end-of-stream after a short grace period.
            try
            {
                await pumps.WaitAsync(DrainGrace, CancellationToken.None);
            }
            catch (TimeoutException)
            {
            }

            return new(process.ExitCode, Snapshot(output), Snapshot(error));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentException($"'{name}' did not finish within {request.Timeout!.Value.TotalSeconds:0} seconds.");
        }
        finally
        {
            // Whatever went wrong, never leave the CLI running behind us.
            if (!process.HasExited)
                await KillAsync(process);
        }
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder sink)
    {
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer)) > 0)
            {
                lock (sink)
                    sink.Append(buffer, 0, read);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The process was killed or disposed mid-read; what was captured so far is all there is.
        }
    }

    private static string Snapshot(StringBuilder sink)
    {
        lock (sink)
            return sink.ToString();
    }

    private static async Task WriteInputAsync(Process process, string? input, CancellationToken cancellationToken)
    {
        try
        {
            if (input is not null)
                await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
            // Always close stdin so a CLI that reads it sees EOF instead of waiting forever.
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The process exited without reading its input; the exit code and stderr tell the story.
        }
    }

    private static async Task KillAsync(Process process)
    {
        try
        {
            // The CLI is often a shim (cmd/node/sh) around the real process, so kill the whole tree.
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(KillGrace);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException or TimeoutException)
        {
            // Best effort: a failed kill must not replace the cancellation or error already in flight.
        }
    }
}
