namespace Relay.Agents.Processes;

/// <summary>
/// The executable search path with relative entries removed. A relative entry (such as <c>.</c>, or an
/// empty entry on Unix) resolves against the working directory, which may be an untrusted repository
/// carrying its own <c>claude</c> or <c>node</c>.
/// </summary>
public static class SearchPath
{
    public static IEnumerable<string> AbsoluteEntries(string? path)
        => (path ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => Path.IsPathFullyQualified(entry.Trim('"')));

    public static string WithoutRelativeEntries(string? path) => string.Join(Path.PathSeparator, AbsoluteEntries(path));
}
