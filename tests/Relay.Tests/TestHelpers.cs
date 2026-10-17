using System.Diagnostics;

namespace Relay.Tests;

internal static class Poll
{
    public static async Task UntilAsync(Func<bool> condition, string description, int timeoutSeconds = 30)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds), $"Timed out waiting until {description}.");
            await Task.Delay(100);
        }
    }

    /// <summary>Waits for a helper script to write the id of the process it started.</summary>
    public static async Task<int> ForProcessIdAsync(string pidFile)
    {
        var pid = 0;
        await UntilAsync(() => File.Exists(pidFile) && int.TryParse(ReadShared(pidFile), out pid), "the child process id is written");
        return pid;
    }

    public static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads a file that another process may be writing at this moment. Returns an empty string when
    /// the file is missing or briefly locked, so a caller that is polling simply tries again.
    /// </summary>
    public static string ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Trim();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}

/// <summary>A test of behaviour only Windows has, such as file sharing, where an open file can refuse to be renamed over.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(string reason = "Relies on Windows file sharing. Elsewhere an open file never blocks a rename.")
    {
        if (!OperatingSystem.IsWindows())
            Skip = reason;
    }
}

/// <summary>A test that creates a symbolic link, which Windows only allows an administrator or a developer-mode account to do.</summary>
public sealed class SymbolicLinkFactAttribute : FactAttribute
{
    private static readonly bool Allowed = CanLink();

    public SymbolicLinkFactAttribute()
    {
        if (!Allowed)
            Skip = "This account may not create symbolic links.";
    }

    private static bool CanLink()
    {
        var directory = Directory.CreateTempSubdirectory("relay-link-").FullName;
        try
        {
            File.CreateSymbolicLink(Path.Combine(directory, "link"), Path.Combine(directory, "target"));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

/// <summary>A test that sends POSIX signals, which Windows has no equivalent for outside a console.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Sends POSIX signals. On Windows the kill path is covered by ProcessTests.";
    }
}
