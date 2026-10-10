using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core.Workspaces;

/// <summary>
/// Remembers which folders the user has said they trust, in a JSON file in their own profile.
/// Trust is per exact folder: trusting a folder says nothing about its parents or children.
/// </summary>
/// <param name="filePath">Null when no user configuration folder exists: nothing is trusted and nothing can be remembered.</param>
/// <remarks>
/// A missing or corrupt file means nothing is trusted. A file that exists but cannot be read (locked,
/// access denied) is an error, never "nothing trusted": acting on that would overwrite the list.
/// </remarks>
public sealed class WorkspaceTrustStore(string? filePath)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// The trust file in the user's configuration folder, or under <c>RELAY_HOME</c> when that is set
    /// to an absolute path. A relative location is never used: it would resolve against the working
    /// directory and let an untrusted folder supply its own trust file.
    /// </summary>
    public static string? DefaultFilePath()
    {
        var home = Environment.GetEnvironmentVariable("RELAY_HOME");
        if (!string.IsNullOrEmpty(home) && Path.IsPathFullyQualified(home))
            return Path.Combine(home, "trusted-folders.json");

        // DoNotVerify: by default an empty string comes back when the folder does not exist yet (a fresh
        // Linux account has no ~/.config), which would silently turn this into a relative path.
        var configuration = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        return Path.IsPathFullyQualified(configuration)
            ? Path.Combine(configuration, "relay", "trusted-folders.json")
            : null;
    }

    /// <exception cref="IOException">The trust file exists but could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The trust file exists but could not be read.</exception>
    public IReadOnlyList<string> TrustedFolders => Read();

    /// <inheritdoc cref="TrustedFolders"/>
    public bool IsTrusted(string folder) => Read().Contains(Normalize(folder), PathComparer);

    /// <exception cref="IOException">The trust file could not be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">The trust file could not be read or written.</exception>
    public void Trust(string folder)
        => Update(folders =>
        {
            if (folders.Contains(Normalize(folder), PathComparer))
                return false;

            folders.Add(Normalize(folder));
            return true;
        });

    /// <returns>False when the folder was not trusted in the first place.</returns>
    /// <inheritdoc cref="Trust"/>
    public bool Revoke(string folder)
        => Update(folders => folders.RemoveAll(f => PathComparer.Equals(f, Normalize(folder))) > 0);

    private static string Normalize(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    private List<string> Read()
    {
        if (filePath is null)
            return [];

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Parse(stream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private bool Update(Func<List<string>, bool> change)
    {
        if (filePath is null)
            throw new IOException("No user configuration folder could be found. Set RELAY_HOME to an absolute path.");

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        // Held exclusively across the read and the write, so two Relay processes cannot lose each other's change.
        using var stream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var folders = Parse(stream);
        if (!change(folders))
            return false;

        stream.SetLength(0);
        JsonSerializer.Serialize(stream, new TrustFile([.. folders]), Indented);
        return true;
    }

    private static List<string> Parse(Stream stream)
    {
        if (stream.Length == 0)
            return [];

        try
        {
            return JsonSerializer.Deserialize<TrustFile>(stream)?.TrustedFolders?.OfType<string>().ToList() ?? [];
        }
        catch (JsonException)
        {
            // Corrupt: nothing is trusted, and the next change replaces the file.
            return [];
        }
    }

    private sealed record TrustFile([property: JsonPropertyName("trustedFolders")] List<string?>? TrustedFolders);
}
