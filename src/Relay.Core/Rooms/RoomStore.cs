using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Relay.Core.Storage;

namespace Relay.Core.Rooms;

/// <summary>A saved room as <c>relay rooms</c> lists it.</summary>
/// <param name="Workspace">The folder the room belongs to. Null, with <paramref name="Messages"/>, when the file could not be read.</param>
public sealed record SavedRoomInfo(string Name, string? Workspace, int? Messages, DateTimeOffset LastSaved, long Size);

/// <summary>
/// Saves rooms the user has named, one file per room and workspace folder, in a folder of the user's
/// own that only they can read. See docs/adr/0009-saved-rooms.md.
/// </summary>
/// <param name="folder">Null when no user data folder exists: nothing can be saved.</param>
/// <remarks>
/// A room belongs to one folder because the agents' CLIs keep a conversation per folder: a room
/// opened elsewhere could not continue it. A file that cannot be parsed is set aside, never
/// overwritten. A file that exists but cannot be read is an error.
/// </remarks>
public sealed partial class RoomStore(string? folder)
{
    public const int MaxNameLength = 64;

    public string? Folder => folder;

    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// <c>rooms</c> under <c>RELAY_HOME</c> when that is an absolute path, otherwise in the user's
    /// local data folder. A relative location is never used: it would resolve against the working
    /// directory, which is not the user's.
    /// </summary>
    public static string? DefaultFolder()
    {
        var home = Environment.GetEnvironmentVariable("RELAY_HOME");
        if (!string.IsNullOrEmpty(home) && Path.IsPathFullyQualified(home))
            return Path.Combine(home, "rooms");

        // Local rather than roaming: a transcript should not follow a roaming profile to other machines.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        return Path.IsPathFullyQualified(local) ? Path.Combine(local, "relay", "rooms") : null;
    }

    /// <summary>
    /// Letters, digits, '-' and '_', starting with a letter or digit. It becomes part of a file name,
    /// so nothing in it can reach another folder.
    /// </summary>
    public static bool IsValidName(string name) => Name().IsMatch(name);

    /// <summary>The room saved for this folder under this name, or a new one, held until the result is disposed.</summary>
    /// <exception cref="IOException">The room is open in another Relay, or its file exists but could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The room's file exists but could not be read.</exception>
    public SavedRoom Open(string workspace, string name)
    {
        name = Canonical(name);
        var path = PathFor(workspace, name);
        PrepareFolder();
        var gate = Hold(path, name);
        try
        {
            RoomState? state;
            string? setAside = null;
            try
            {
                state = Parse(File.ReadAllBytes(path), workspace, name);
                if (state is null)
                {
                    setAside = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}";
                    File.Move(path, setAside);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                state = null;
            }

            var room = state is null ? new ChatRoom(Guid.NewGuid(), name) : ChatRoom.Restore(state);
            return new SavedRoom(room, path, Normalize(workspace), gate, state is not null, setAside);
        }
        catch
        {
            gate.Dispose();
            throw;
        }
    }

    /// <exception cref="IOException">The folder could not be listed.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder could not be listed.</exception>
    public IReadOnlyList<SavedRoomInfo> List()
    {
        if (folder is null || !Directory.Exists(folder))
            return [];

        List<SavedRoomInfo> rooms = [];
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            var info = new FileInfo(path);
            RoomFile? file = null;
            try
            {
                file = JsonSerializer.Deserialize<RoomFile>(File.ReadAllBytes(path), Options);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
            }

            // The name after the folder's hash, for a file that says nothing itself.
            var fallback = Path.GetFileNameWithoutExtension(path) is var stem && stem.IndexOf('-') is var dash and >= 0 ? stem[(dash + 1)..] : stem;
            rooms.Add(file is { Name: { } name, Workspace: { } workspace, Messages: { } messages }
                ? new(name, workspace, messages.Count, info.LastWriteTimeUtc, info.Length)
                : new(fallback, null, null, info.LastWriteTimeUtc, info.Length));
        }

        return [.. rooms.OrderBy(r => r.Workspace, PathComparer).ThenBy(r => r.Name, StringComparer.Ordinal)];
    }

    /// <summary>Deletes the room saved for this folder under this name, with any copy of it set aside.</summary>
    /// <returns>False when there was nothing to delete.</returns>
    /// <exception cref="IOException">The room is open in another Relay, or could not be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The room could not be deleted.</exception>
    public bool Delete(string workspace, string name)
    {
        name = Canonical(name);
        var path = PathFor(workspace, name);
        if (!Directory.Exists(folder))
            return false;

        var file = Path.GetFileName(path);
        var lockPath = path + ".lock";
        var deleted = false;
        using (Hold(path, name))
        {
            foreach (var each in Directory.EnumerateFiles(folder, file + "*"))
            {
                if (each == lockPath || !Path.GetFileName(each).StartsWith(file, StringComparison.Ordinal))
                    continue;
                File.Delete(each);
                deleted = true;
            }
        }

        // Only once it is released: Windows will not delete a file that is open. If another Relay has
        // opened it in the meantime, it stays, empty, and that Relay holds it.
        try
        {
            File.Delete(lockPath);
        }
        catch (IOException)
        {
        }

        return deleted;
    }

    internal static void Write(string path, string workspace, RoomState state)
        => AtomicFile.Write(path, stream =>
        {
            // On Windows the file takes the folder's owner-only access as it is created.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            JsonSerializer.Serialize(stream, RoomFile.From(workspace, state), Options);
        });

    private static string Canonical(string name)
    {
        if (!IsValidName(name))
            throw new ArgumentException($"'{name}' is not a room name.", nameof(name));
        return name.ToLowerInvariant();
    }

    private string PathFor(string workspace, string name)
    {
        if (folder is null)
            throw new IOException("No user data folder could be found. Set RELAY_HOME to an absolute path.");

        var key = Normalize(workspace);
        if (OperatingSystem.IsWindows())
            key = key.ToUpperInvariant();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 8);
        return Path.Combine(folder, $"{hash}-{name}.json");
    }

    private void PrepareFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder!);
            RestrictToOwner(folder!);
            return;
        }

        Directory.CreateDirectory(folder!, OwnerOnly);
        // Also when it already existed, perhaps made by hand with the usual permissions.
        File.SetUnixFileMode(folder!, OwnerOnly);
    }

    // Inherited by everything created inside, so a transcript is never readable by another account,
    // even for the moment between its creation and a change to its own permissions.
    [SupportedOSPlatform("windows")]
    private static void RestrictToOwner(string directory)
    {
        var access = new DirectorySecurity();
        access.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        access.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(access);
    }

    // Held for as long as the room is open, so two Relays can never both write it and lose each
    // other's messages. Beside the file, because the file itself is replaced on every save.
    private static FileStream Hold(string path, string name)
    {
        try
        {
            return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new IOException($"Room {name} is open in another Relay for this folder, or its lock could not be taken ({ex.Message}).", ex);
        }
    }

    private static string Normalize(string workspace) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));

    // Anything that does not hold together is treated as corrupt, not repaired: a guess at what a
    // damaged file meant could show an agent the wrong messages or none.
    private static RoomState? Parse(byte[] bytes, string workspace, string name)
    {
        RoomFile? file;
        try
        {
            file = JsonSerializer.Deserialize<RoomFile>(bytes, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (file is not { Version: RoomFile.CurrentVersion, Workspace: { } owner, Messages: { } entries, Agents: { } agentEntries }
            || !PathComparer.Equals(owner, Normalize(workspace))
            || file.Name != name)
            return null;

        List<ChatMessage> messages = [];
        HashSet<Guid> ids = [];
        foreach (var entry in entries)
        {
            if (entry is not { Sender.Length: > 0, Content: { } content }
                || !Enum.IsDefined(entry.Kind)
                || entry.AddressLength < 0
                || entry.AddressLength > content.Length
                || !ids.Add(entry.Id))
                return null;
            messages.Add(new(entry.Id, file.Id, entry.Sender, entry.Kind, content, entry.Time, entry.Target, entry.AddressLength));
        }

        List<RoomAgentState> agents = [];
        HashSet<string> agentIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in agentEntries)
        {
            if (entry is not { Id.Length: > 0 } || !agentIds.Add(entry.Id) || entry.ResetAt < 0 || entry.ResetAt > messages.Count)
                return null;

            var seen = entry.Seen ?? [];
            var sends = entry.Sends ?? [];
            if (!seen.All(ids.Contains) || sends.Any(s => !ids.Contains(s.Key) || s.Value < 1))
                return null;

            AgentRole? role = null;
            if (entry.Role is { } held && !TryRestore(held, out role))
                return null;

            agents.Add(new(entry.Id, entry.Conversation, [.. seen], sends, entry.ResetAt, role, entry.RoleStated, entry.RoleWithdrawn));
        }

        return new(file.Id, name, messages, agents);
    }

    // Held to the same rules as a role the user types, except that a built-in role keeps its own text.
    private static bool TryRestore(RoleEntry entry, out AgentRole? role)
    {
        role = null;
        if (entry is not { Name: { } name, Description: { } description })
            return false;

        if (AgentRole.FindBuiltIn(name) is { } builtIn && builtIn.Description == description)
        {
            role = builtIn;
            return true;
        }

        return AgentRole.TryCreate(name, description, out role, out _);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex Name();

    private sealed record RoomFile(int Version, string? Workspace, Guid Id, string? Name, List<MessageEntry?>? Messages, List<AgentEntry?>? Agents)
    {
        public const int CurrentVersion = 1;

        public static RoomFile From(string workspace, RoomState state)
            => new(
                CurrentVersion,
                workspace,
                state.Id,
                state.Name,
                [.. state.Messages.Select(m => new MessageEntry(m.Id, m.SenderId, m.SenderType, m.Content, m.Timestamp, m.TargetId, m.AddressLength))],
                [.. state.Agents.Select(a => new AgentEntry(
                    a.Id,
                    a.ConversationId,
                    [.. a.Seen],
                    new(a.Sends),
                    a.ResetAt,
                    a.Role is { } role ? new RoleEntry(role.Name, role.Description) : null,
                    a.RoleStated,
                    a.RoleWithdrawn))]);
    }

    private sealed record MessageEntry(Guid Id, string? Sender, SenderType Kind, string? Content, DateTimeOffset Time, string? Target, int AddressLength);

    private sealed record AgentEntry(string? Id, string? Conversation, List<Guid>? Seen, Dictionary<Guid, int>? Sends, int ResetAt, RoleEntry? Role, bool RoleStated, bool RoleWithdrawn);

    private sealed record RoleEntry(string? Name, string? Description);
}

/// <summary>
/// A room opened from <see cref="RoomStore"/>. Every change to it is saved as it happens, until this
/// is disposed, and no other Relay can open it meanwhile.
/// </summary>
public sealed class SavedRoom : IDisposable
{
    private readonly Lock _saving = new();
    private readonly string _workspace;
    private readonly FileStream _gate;
    private bool _failing;
    private bool _disposed;

    internal SavedRoom(ChatRoom room, string filePath, string workspace, FileStream gate, bool reopened, string? setAside)
    {
        Room = room;
        FilePath = filePath;
        Reopened = reopened;
        SetAside = setAside;
        _workspace = workspace;
        _gate = gate;
        room.Changed += Save;
    }

    public ChatRoom Room { get; }

    public string FilePath { get; }

    /// <summary>Whether the room was read back from an earlier run, rather than started empty.</summary>
    public bool Reopened { get; }

    /// <summary>Where a saved room that could not be read was moved, when it could not.</summary>
    public string? SetAside { get; }

    /// <summary>
    /// Raised with the reason when saving starts to fail, and with null when it works again. The room
    /// goes on in memory either way: losing the disk must not end the conversation.
    /// </summary>
    public event Action<Exception?>? SaveStatusChanged;

    private void Save()
    {
        bool changed;
        Exception? failure = null;
        lock (_saving)
        {
            if (_disposed)
                return;

            try
            {
                RoomStore.Write(FilePath, _workspace, Room.Snapshot());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex;
            }

            changed = _failing != failure is not null;
            _failing = failure is not null;
        }

        if (changed)
            SaveStatusChanged?.Invoke(failure);
    }

    public void Dispose()
    {
        lock (_saving)
        {
            if (_disposed)
                return;

            _disposed = true;
            Room.Changed -= Save;
            _gate.Dispose();
        }
    }
}
