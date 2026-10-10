using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core.Workspaces;

/// <summary>
/// Remembers which folders the user has said they trust, in a JSON file in their own profile.
/// Trust is per exact folder: trusting a folder says nothing about its parents or children.
/// </summary>
public sealed class WorkspaceTrustStore(string filePath)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public string FilePath => filePath;

    public IReadOnlyList<string> TrustedFolders => Load();

    /// <summary>
    /// The trust file in the user's configuration folder, or under <c>RELAY_HOME</c> when that is set
    /// to an absolute path. A relative value is ignored: it would resolve against the working
    /// directory and let an untrusted folder supply its own trust file.
    /// </summary>
    public static string DefaultFilePath()
    {
        var home = Environment.GetEnvironmentVariable("RELAY_HOME");
        if (!string.IsNullOrEmpty(home) && Path.IsPathFullyQualified(home))
            return Path.Combine(home, "trusted-folders.json");

        // DoNotVerify: by default an empty string comes back when the folder does not exist yet (a fresh
        // Linux account has no ~/.config), which would silently turn this into a relative path.
        var configuration = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (!Path.IsPathFullyQualified(configuration))
            throw new InvalidOperationException("No user configuration folder could be found. Set RELAY_HOME to an absolute path.");

        return Path.Combine(configuration, "relay", "trusted-folders.json");
    }

    public bool IsTrusted(string folder) => Load().Contains(Normalize(folder), PathComparer);

    /// <exception cref="IOException">The trust file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The trust file could not be written.</exception>
    public void Trust(string folder)
    {
        var folders = Load();
        if (folders.Contains(Normalize(folder), PathComparer))
            return;

        folders.Add(Normalize(folder));
        Save(folders);
    }

    /// <returns>False when the folder was not trusted in the first place.</returns>
    public bool Revoke(string folder)
    {
        var folders = Load();
        if (folders.RemoveAll(f => PathComparer.Equals(f, Normalize(folder))) == 0)
            return false;

        Save(folders);
        return true;
    }

    private static string Normalize(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    private List<string> Load()
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            return JsonSerializer.Deserialize<TrustFile>(stream)?.TrustedFolders?.OfType<string>().ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing, unreadable or corrupt: nothing is trusted.
            return [];
        }
    }

    private void Save(List<string> folders)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, JsonSerializer.Serialize(new TrustFile([.. folders]), Indented));
    }

    private sealed record TrustFile([property: JsonPropertyName("trustedFolders")] List<string?>? TrustedFolders);
}
