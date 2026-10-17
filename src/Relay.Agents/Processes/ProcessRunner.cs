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

    /// <summary>
    /// Called with each line of standard output as the process writes it, without its line ending,
    /// from the thread that reads the output. The whole output is still returned at the end.
    /// It is never called once the run has ended, and not again in a run in which it threw.
    /// </summary>
    public Action<string>? OnOutputLine { get; init; }
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
        if (OperatingSystem.IsWindows())
        {
            try
            {
                KillOnExitJob.Join();
            }
            catch (Win32Exception ex)
            {
                // Started anyway, the agent would keep running if Relay were killed.
                throw new AgentException(
                    $"Did not start '{name}': Relay could not make sure it stops when Relay does ({ex.Message}).", ex) { PromptMayHaveBeenRead = false };
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new AgentException($"Could not start '{name}': {ex.Message}", ex) { PromptMayHaveBeenRead = false };
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout is { } limit)
            timeout.CancelAfter(limit);

        var output = new StringBuilder();
        var error = new StringBuilder();
        var lines = request.OnOutputLine is { } onLine ? new OutputLines(onLine) : null;
        var pumps = Task.WhenAll(PumpAsync(process.StandardOutput, output, lines), PumpAsync(process.StandardError, error));
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
            throw new AgentException($"'{name}' did not finish within {Describe(request.Timeout!.Value)} and was stopped.") { TimedOut = true };
        }
        finally
        {
            // Whatever went wrong, never leave the CLI running behind us.
            if (!process.HasExited)
                await KillAsync(process);

            // A pump can outlive the run, reading from a descendant that kept the pipe open, and
            // the caller has moved on: nothing it reads from here on is part of this run.
            lines?.Stop();
        }
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder sink, OutputLines? lines = null)
    {
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer)) > 0)
            {
                lock (sink)
                    sink.Append(buffer, 0, read);
                lines?.Append(buffer.AsSpan(0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The process was killed or disposed mid-read; what was captured so far is all there is.
        }

        lines?.End();
    }

    /// <summary>Splits output into lines as it arrives and hands each to the caller, until the run ends.</summary>
    private sealed class OutputLines(Action<string> onLine)
    {
        private readonly Lock _gate = new();
        private readonly StringBuilder _line = new();
        private bool _stopped;

        public void Append(ReadOnlySpan<char> text)
        {
            int end;
            while ((end = text.IndexOf('\n')) >= 0)
            {
                _line.Append(text[..end]);
                Report();
                text = text[(end + 1)..];
            }

            _line.Append(text);
        }

        /// <summary>The output has ended, so what is left is a last line without a line ending.</summary>
        public void End()
        {
            if (_line.Length > 0)
                Report();
        }

        /// <summary>Returns once no call to the caller is in progress, and none will be made again.</summary>
        public void Stop()
        {
            lock (_gate)
                _stopped = true;
        }

        private void Report()
        {
            var line = _line.ToString().TrimEnd('\r');
            _line.Clear();
            lock (_gate)
            {
                if (_stopped)
                    return;

                try
                {
                    onLine(line);
                }
                catch (Exception)
                {
                    // The caller is being told about the output, and the output must be read to its
                    // end whether or not it can listen: a process whose pipe is not read stops when the
                    // pipe fills and never exits. So the caller is not told again, and the run goes on.
                    _stopped = true;
                }
            }
        }
    }

    private static string Describe(TimeSpan duration) => duration switch
    {
        { TotalHours: >= 1 } when duration.Minutes == 0 && duration.Seconds == 0 => Plural((int)duration.TotalHours, "hour"),
        { TotalMinutes: >= 1 } when duration.Seconds == 0 => Plural((int)duration.TotalMinutes, "minute"),
        _ => Plural((int)Math.Ceiling(duration.TotalSeconds), "second"),
    };

    private static string Plural(int count, string unit) => count == 1 ? $"1 {unit}" : $"{count} {unit}s";

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
