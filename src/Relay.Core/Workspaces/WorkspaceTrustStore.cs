using System.Runtime.InteropServices;
using System.Security.AccessControl;
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
/// A change is staged beside the file and renamed over it, so an interrupted write leaves either
/// the previous list or the new one, both complete, never a truncated one that would read as corrupt.
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

        // A lock of its own, because the list is replaced rather than rewritten and so cannot be held
        // open across its own replacement. Held across the read and the rename, so two Relay processes
        // cannot lose each other's change. Opened for writing although nothing is written: over NFS an
        // exclusive lock is only granted on a writable handle.
        using var gate = new FileStream(filePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var folders = Read();
        if (!change(folders))
            return false;

        // When the list is a link (a configuration folder kept in a dotfiles repository), the file it
        // points at is the one replaced. Renaming over the link itself would quietly unlink the two.
        var link = new FileInfo(filePath);
        var target = link.LinkTarget is null ? filePath : link.ResolveLinkTarget(returnFinalTarget: true)!.FullName;

        // Written whole and then renamed over the list, so an interrupted write cannot leave the list
        // truncated. A truncated list parses as corrupt, which would mean nothing is trusted any more.
        var staging = target + ".new";
        try
        {
            using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                // Before anything is written, so the new list is never readable by more people than the old one.
                CopyPermissions(target, stream);
                JsonSerializer.Serialize(stream, new TrustFile([.. folders]), Indented);
                stream.Flush(flushToDisk: true);
            }

            MoveOver(staging, target);
        }
        finally
        {
            File.Delete(staging);
        }

        if (!OperatingSystem.IsWindows())
            FlushDirectory(Path.GetDirectoryName(target)!);

        return true;
    }

    /// <summary>The new list replaces the old file, so whatever the user restricted on the old one has to be carried over.</summary>
    private static void CopyPermissions(string from, FileStream to)
    {
        if (!File.Exists(from))
            return;

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(to.SafeFileHandle, File.GetUnixFileMode(from));
            return;
        }

        // Rebuilt from its text form: a FileSecurity read from one file saves nothing when applied to another.
        var access = new FileSecurity();
        access.SetSecurityDescriptorSddlForm(
            new FileInfo(from).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access),
            AccessControlSections.Access);
        new FileInfo(to.Name).SetAccessControl(access);
    }

    private static void MoveOver(string staging, string target)
    {
        // Windows refuses the rename while either file is open: the list for as long as another Relay
        // takes to read it, the staging file for as long as a virus scanner or the search indexer looks
        // at what was just written. Both pass in a moment.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(staging, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }

    /// <summary>
    /// Makes the rename itself survive a power loss, not just the new file's contents. Best effort:
    /// the list is complete either way, and not every file system can flush a directory.
    /// </summary>
    private static void FlushDirectory(string directory)
    {
        var descriptor = open(directory, 0);
        if (descriptor < 0)
            return;

        _ = fsync(descriptor);
        _ = close(descriptor);
    }

    [DllImport("libc")]
    private static extern int open(string path, int flags);

    [DllImport("libc")]
    private static extern int fsync(int descriptor);

    [DllImport("libc")]
    private static extern int close(int descriptor);

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
