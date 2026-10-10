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

        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await WriteInputAsync(process, request.StandardInput, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            await KillAsync(process, stdout, stderr);
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new AgentException($"'{name}' did not finish within {request.Timeout!.Value.TotalSeconds:0} seconds.");
        }
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

    private static async Task KillAsync(Process process, Task<string> stdout, Task<string> stderr)
    {
        try
        {
            // The CLI is often a shim (cmd/node/sh) around the real process, so kill the whole tree.
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
        }

        try
        {
            await Task.WhenAll(stdout, stderr);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
        }
    }
}
