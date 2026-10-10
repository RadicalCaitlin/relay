namespace Relay.Agents.Processes;

public interface IExecutableLocator
{
    /// <summary>Returns the full path the operating system would resolve <paramref name="command"/> to, or null.</summary>
    string? Find(string command);
}

public sealed class PathExecutableLocator : IExecutableLocator
{
    // PATHEXT may also list script types (.JS, .VBS, .PS1) that only a shell can launch.
    private static readonly string[] LaunchableExtensions = [".COM", ".EXE", ".BAT", ".CMD"];

    public string? Find(string command)
        => Find(command, Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT"));

    public static string? Find(string command, string? path, string? pathExt)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        var directories = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in directories)
        {
            // A relative entry such as "." resolves against the working directory, which may be an
            // untrusted repository carrying its own "claude" executable.
            var directory = entry.Trim('"');
            if (!Path.IsPathFullyQualified(directory))
                continue;

            foreach (var extension in Extensions(pathExt))
            {
                var candidate = Path.Combine(directory, command + extension);
                if (IsExecutable(candidate))
                    return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string> Extensions(string? pathExt)
    {
        if (!OperatingSystem.IsWindows())
            return [string.Empty];

        var configured = (pathExt ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var launchable = configured.Where(e => LaunchableExtensions.Contains(e, StringComparer.OrdinalIgnoreCase)).ToArray();
        return launchable.Length > 0 ? launchable : LaunchableExtensions;
    }

    private static bool IsExecutable(string candidate)
    {
        if (!File.Exists(candidate))
            return false;
        if (OperatingSystem.IsWindows())
            return true;

        const UnixFileMode execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(candidate) & execute) != 0;
    }
}
