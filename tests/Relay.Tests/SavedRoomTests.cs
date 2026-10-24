using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Relay.Core.Rooms;
using Relay.Core.Routing;

namespace Relay.Tests;

public sealed class SavedRoomTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("relay-rooms-").FullName;
    private readonly string _rooms;
    private readonly string _folder;

    public SavedRoomTests()
    {
        _rooms = Path.Combine(_directory, "home", "rooms");
        _folder = Directory.CreateDirectory(Path.Combine(_directory, "repo")).FullName;
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows() && Directory.Exists(_rooms))
            File.SetUnixFileMode(_rooms, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(_directory, recursive: true);
    }

    private RoomStore Store() => new(_rooms);

    private static (AgentParticipant Agent, FakeSession Session) Agent(string id, string? conversation = null, bool canResume = true)
    {
        var session = new FakeSession($"hello from {id}") { ConversationId = conversation, CanResume = canResume, RemembersConversation = canResume };
        return (new AgentParticipant(id, id.ToUpperInvariant(), session), session);
    }

    // One exchange with Claude, saved as it happens.
    private async Task<string> SaveOneExchangeAsync()
    {
        using var saved = Store().Open(_folder, "work");
        var (claude, _) = Agent("claude", "conv-claude");
        saved.Room.AddParticipant(claude);
        await new MessageRouter().SendHumanMessageAsync(saved.Room, "@claude hi");
        return saved.FilePath;
    }

    [Fact]
    public async Task A_room_and_each_agents_conversation_survive_a_restart()
    {
        await SaveOneExchangeAsync();

        using var reopened = Store().Open(_folder, "work");
        var (claude, session) = Agent("claude");
        reopened.Room.AddParticipant(claude);

        Assert.True(reopened.Reopened);
        Assert.Equal("work", reopened.Room.Name);
        Assert.Equal(["@claude hi", "hello from claude"], reopened.Room.Messages.Select(m => m.Content));
        Assert.Equal("conv-claude", session.ConversationId);
        // Its conversation holds both, so neither is sent to it again.
        Assert.Empty(reopened.Room.UnseenBy(claude).Messages);
    }

    [Fact]
    public async Task Every_change_is_saved_as_it_happens_not_only_when_Relay_leaves()
    {
        using var saved = Store().Open(_folder, "work");
        var (claude, _) = Agent("claude");
        saved.Room.AddParticipant(claude);

        await new MessageRouter().SendHumanMessageAsync(saved.Room, "@claude hi");

        // Still open, as it would be in a Relay that is then killed.
        Assert.Equal(2, Assert.Single(Store().List()).Messages);
    }

    [Fact]
    public async Task An_agent_whose_conversation_cannot_be_continued_starts_again_and_is_shown_the_room()
    {
        await SaveOneExchangeAsync();

        using var reopened = Store().Open(_folder, "work");
        var (claude, session) = Agent("claude", canResume: false);
        reopened.Room.AddParticipant(claude);

        Assert.Null(session.ConversationId);
        Assert.Equal(2, reopened.Room.UnseenBy(claude).Messages.Count);
    }

    [Fact]
    public async Task What_reset_hid_stays_hidden_when_the_conversation_cannot_be_continued()
    {
        using (var saved = Store().Open(_folder, "work"))
        {
            var (claude, _) = Agent("claude", "conv-claude");
            saved.Room.AddParticipant(claude);
            await new MessageRouter().SendHumanMessageAsync(saved.Room, "@claude secret plan");
            saved.Room.ResetConversation(claude);
            saved.Room.AddMessage(ChatRoom.HumanId, SenderType.Human, "after the reset");
        }

        using var reopened = Store().Open(_folder, "work");
        var (again, _) = Agent("claude", canResume: false);
        reopened.Room.AddParticipant(again);

        Assert.Equal(["after the reset"], reopened.Room.UnseenBy(again).Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task Reset_forgets_the_conversation_in_the_saved_room_too()
    {
        using (var saved = Store().Open(_folder, "work"))
        {
            var (claude, _) = Agent("claude", "conv-claude");
            saved.Room.AddParticipant(claude);
            await new MessageRouter().SendHumanMessageAsync(saved.Room, "@claude hi");
            saved.Room.RestartConversation(claude);
        }

        using var reopened = Store().Open(_folder, "work");
        var (again, session) = Agent("claude");
        reopened.Room.AddParticipant(again);

        Assert.Null(session.ConversationId);
        Assert.Equal(2, reopened.Room.UnseenBy(again).Messages.Count);
    }

    [Fact]
    public void Roles_are_saved_with_the_room()
    {
        var custom = AgentRole.TryCreate("tester", "You write the tests.", out var role, out _) ? role! : throw new InvalidOperationException();
        using (var saved = Store().Open(_folder, "work"))
        {
            var (claude, _) = Agent("claude", "conv-claude");
            var (codex, _) = Agent("codex", "conv-codex");
            saved.Room.AddParticipant(claude);
            saved.Room.AddParticipant(codex);
            saved.Room.SetRole(claude, AgentRole.FindBuiltIn("reviewer"));
            saved.Room.SetRole(codex, custom);
        }

        using var reopened = Store().Open(_folder, "work");
        var (claudeAgain, _) = Agent("claude");
        var (codexAgain, _) = Agent("codex");
        reopened.Room.AddParticipant(claudeAgain);
        reopened.Room.AddParticipant(codexAgain);

        Assert.Same(AgentRole.FindBuiltIn("reviewer"), reopened.Room.RoleOf(claudeAgain));
        Assert.Equal(custom, reopened.Room.RoleOf(codexAgain));
    }

    [Fact]
    public async Task An_agent_left_out_of_one_run_keeps_its_conversation_for_the_next()
    {
        using (var saved = Store().Open(_folder, "work"))
        {
            var (codex, _) = Agent("codex", "conv-codex");
            saved.Room.AddParticipant(codex);
            await new MessageRouter().SendHumanMessageAsync(saved.Room, "@codex hi");
        }

        using (var withoutCodex = Store().Open(_folder, "work"))
        {
            var (claude, _) = Agent("claude", "conv-claude");
            withoutCodex.Room.AddParticipant(claude);
            await new MessageRouter().SendHumanMessageAsync(withoutCodex.Room, "@claude hi");
        }

        using var reopened = Store().Open(_folder, "work");
        var (codexAgain, session) = Agent("codex");
        reopened.Room.AddParticipant(codexAgain);

        Assert.Equal("conv-codex", session.ConversationId);
        Assert.Equal(["@claude hi", "hello from claude"], reopened.Room.UnseenBy(codexAgain).Messages.Select(m => m.Content));
    }

    [Fact]
    public void A_room_belongs_to_one_folder()
    {
        var other = Directory.CreateDirectory(Path.Combine(_directory, "other")).FullName;
        using (var saved = Store().Open(_folder, "work"))
            saved.Room.AddMessage(ChatRoom.HumanId, SenderType.Human, "only here");

        using var elsewhere = Store().Open(other, "work");
        using var again = Store().Open(_folder + Path.DirectorySeparatorChar, "WORK");

        Assert.False(elsewhere.Reopened);
        Assert.Empty(elsewhere.Room.Messages);
        Assert.True(again.Reopened);
    }

    [Fact]
    public void A_room_open_in_one_Relay_cannot_be_opened_in_another()
    {
        using (var first = Store().Open(_folder, "work"))
        {
            var refused = Assert.Throws<IOException>(() => Store().Open(_folder, "work"));
            Assert.Contains("open in another Relay", refused.Message);
        }

        using var afterwards = Store().Open(_folder, "work");
    }

    [Theory]
    [InlineData("../work")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("-work")]
    [InlineData("_work")]
    [InlineData("work.json")]
    [InlineData("")]
    [InlineData("wKrk")]
    public void A_name_that_could_reach_another_file_is_refused(string name)
    {
        Assert.False(RoomStore.IsValidName(name));
        Assert.Throws<ArgumentException>(() => Store().Open(_folder, name));
        Assert.False(Directory.Exists(_rooms));
    }

    [Fact]
    public void A_name_is_at_most_64_characters()
    {
        Assert.True(RoomStore.IsValidName(new string('a', RoomStore.MaxNameLength)));
        Assert.False(RoomStore.IsValidName(new string('a', RoomStore.MaxNameLength + 1)));
    }

    public static TheoryData<string> Corruptions => new()
    {
        "not json",
        "{}",
        "[]",
        "version=2",
        "workspace=elsewhere",
        "address-past-end",
        "seen-unknown",
        "reset-past-end",
        "role-too-long",
        "duplicate-message",
    };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public async Task A_room_that_does_not_hold_together_is_set_aside_not_overwritten(string corruption)
    {
        var path = await SaveOneExchangeAsync();
        var content = Corrupt(File.ReadAllText(path), corruption);
        File.WriteAllText(path, content);

        using var reopened = Store().Open(_folder, "work");

        Assert.False(reopened.Reopened);
        Assert.Empty(reopened.Room.Messages);
        Assert.NotNull(reopened.SetAside);
        Assert.Equal(content, File.ReadAllText(reopened.SetAside));
        Assert.False(File.Exists(path));
    }

    private static string Corrupt(string json, string corruption)
    {
        if (corruption is "not json" or "{}" or "[]")
            return corruption;

        var room = JsonNode.Parse(json)!;
        var messages = room["messages"]!.AsArray();
        var agent = room["agents"]![0]!;
        switch (corruption)
        {
            case "version=2":
                room["version"] = 2;
                break;
            case "workspace=elsewhere":
                room["workspace"] = Path.GetTempPath();
                break;
            case "address-past-end":
                messages[0]!["addressLength"] = 1000;
                break;
            case "seen-unknown":
                agent["seen"]!.AsArray().Add(Guid.NewGuid().ToString());
                break;
            case "reset-past-end":
                agent["resetAt"] = 3;
                break;
            case "role-too-long":
                agent["role"] = new JsonObject { ["name"] = "mine", ["description"] = new string('x', AgentRole.MaxDescriptionLength + 1) };
                break;
            case "duplicate-message":
                messages.Add(messages[0]!.DeepClone());
                break;
        }

        return room.ToJsonString();
    }

    [WindowsFact]
    public async Task A_room_that_cannot_be_read_is_an_error_and_is_left_untouched()
    {
        var path = await SaveOneExchangeAsync();
        var before = File.ReadAllText(path);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => Store().Open(_folder, "work"));

        Assert.Equal(before, File.ReadAllText(path));
        using var reopened = Store().Open(_folder, "work");
        Assert.True(reopened.Reopened);
    }

    [Fact]
    public void A_failed_save_is_reported_once_and_the_next_one_that_works_catches_up()
    {
        using var saved = Store().Open(_folder, "work");
        List<Exception?> reports = [];
        saved.SaveStatusChanged += reports.Add;
        saved.Room.AddMessage(ChatRoom.HumanId, SenderType.Human, "first");

        using (BlockSaving(saved.FilePath))
        {
            saved.Room.AddMessage(ChatRoom.HumanId, SenderType.Human, "second");
            saved.Room.AddMessage(ChatRoom.HumanId, SenderType.Human, "third");
        }

        Assert.IsAssignableFrom<Exception>(Assert.Single(reports));
        saved.Room.AddMessage(ChatRoom.HumanId, SenderType.Human, "fourth");

        Assert.Equal(2, reports.Count);
        Assert.Null(reports[1]);
        Assert.Equal(4, Assert.Single(Store().List()).Messages);
    }

    // Windows will not rename over a file that is open; elsewhere a folder that cannot be written to
    // takes no new file.
    private IDisposable BlockSaving(string path)
    {
        if (OperatingSystem.IsWindows())
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        File.SetUnixFileMode(_rooms, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return new Unblock(() =>
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_rooms, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        });
    }

    private sealed class Unblock(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    [Fact]
    public async Task Only_the_user_can_read_a_saved_room()
    {
        var path = await SaveOneExchangeAsync();

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_rooms));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            return;
        }

        var user = WindowsIdentity.GetCurrent().User!;
        foreach (var access in (FileSystemSecurity[])[new DirectoryInfo(_rooms).GetAccessControl(), new FileInfo(path).GetAccessControl()])
        {
            var rules = access.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            Assert.NotEmpty(rules);
            foreach (var rule in rules)
                Assert.Equal(user, rule.IdentityReference);
        }

        Assert.True(new DirectoryInfo(_rooms).GetAccessControl().AreAccessRulesProtected);
    }

    [Fact]
    public async Task List_shows_each_saved_room_and_which_folder_it_belongs_to()
    {
        await SaveOneExchangeAsync();
        using (var broken = Store().Open(_folder, "broken"))
            broken.Room.AddMessage(ChatRoom.HumanId, SenderType.Human, "x");
        File.WriteAllText(Directory.EnumerateFiles(_rooms, "*-broken.json").Single(), "not json");

        var rooms = Store().List();

        Assert.Equal(2, rooms.Count);
        var work = rooms.Single(r => r.Name == "work");
        Assert.Equal(_folder, work.Workspace);
        Assert.Equal(2, work.Messages);
        var unreadable = rooms.Single(r => r.Name == "broken");
        Assert.Null(unreadable.Workspace);
        Assert.Null(unreadable.Messages);
    }

    [Fact]
    public void Nothing_is_listed_or_written_before_a_room_is_saved()
    {
        Assert.Empty(Store().List());
        Assert.False(Store().Delete(_folder, "work"));
        Assert.False(Directory.Exists(_rooms));
    }

    [Fact]
    public async Task Delete_removes_the_room_and_every_copy_of_it()
    {
        var path = await SaveOneExchangeAsync();
        File.WriteAllText(path, "not json");
        using (Store().Open(_folder, "work"))
        {
        }

        await SaveOneExchangeAsync();
        Assert.Equal(2, Directory.EnumerateFiles(_rooms, "*.json*").Count(f => !f.EndsWith(".lock", StringComparison.Ordinal)));

        Assert.True(Store().Delete(_folder, "work"));

        Assert.Empty(Directory.EnumerateFiles(_rooms));
        Assert.False(Store().Delete(_folder, "work"));
    }

    [Fact]
    public async Task A_room_open_in_another_Relay_is_not_deleted()
    {
        var path = await SaveOneExchangeAsync();
        using var open = Store().Open(_folder, "work");

        Assert.Throws<IOException>(() => Store().Delete(_folder, "work"));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Without_a_data_folder_nothing_can_be_saved()
    {
        var store = new RoomStore(null);

        Assert.Empty(store.List());
        Assert.Throws<IOException>(() => store.Open(_folder, "work"));
    }
}
