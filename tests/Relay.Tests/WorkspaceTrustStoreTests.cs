using Relay.Core.Workspaces;

namespace Relay.Tests;

public sealed class WorkspaceTrustStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("relay-trust-").FullName;
    private readonly string _file;
    private readonly string _folder;

    public WorkspaceTrustStoreTests()
    {
        _file = Path.Combine(_directory, "config", "trusted-folders.json");
        _folder = Directory.CreateDirectory(Path.Combine(_directory, "repo")).FullName;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private WorkspaceTrustStore Store() => new(_file);

    [Fact]
    public void Nothing_is_trusted_until_the_user_says_so()
    {
        Assert.False(Store().IsTrusted(_folder));
        Assert.Empty(Store().TrustedFolders);
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public void Trust_is_remembered_across_runs()
    {
        Store().Trust(_folder);
        Store().Trust(_folder + Path.DirectorySeparatorChar);

        Assert.True(Store().IsTrusted(_folder));
        Assert.Equal([_folder], Store().TrustedFolders);
    }

    [Fact]
    public void Trust_covers_only_the_exact_folder()
    {
        var child = Directory.CreateDirectory(Path.Combine(_folder, "child")).FullName;
        Store().Trust(_folder);

        Assert.False(Store().IsTrusted(child));
        Assert.False(Store().IsTrusted(_directory));
        Assert.False(Store().IsTrusted(_folder + "-other"));
    }

    [Fact]
    public void Revoke_forgets_a_folder_and_reports_whether_it_was_trusted()
    {
        Store().Trust(_folder);

        Assert.True(Store().Revoke(_folder));
        Assert.False(Store().Revoke(_folder));
        Assert.False(Store().IsTrusted(_folder));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"trustedFolders":null}""")]
    [InlineData("""{"trustedFolders":[null, 42]}""")]
    [InlineData("[]")]
    public void A_corrupt_trust_file_trusts_nothing_and_can_be_replaced(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, content);

        Assert.False(Store().IsTrusted(_folder));

        Store().Trust(_folder);
        Assert.True(Store().IsTrusted(_folder));
    }

    [Fact]
    public void A_trust_file_that_cannot_be_read_is_an_error_and_is_left_untouched()
    {
        var other = Directory.CreateDirectory(Path.Combine(_directory, "other")).FullName;
        Store().Trust(other);
        var before = File.ReadAllText(_file);

        using (new FileStream(_file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => Store().IsTrusted(_folder));
            Assert.ThrowsAny<IOException>(() => Store().Trust(_folder));
            Assert.ThrowsAny<IOException>(() => Store().Revoke(other));
        }

        Assert.Equal(before, File.ReadAllText(_file));
        Assert.True(Store().IsTrusted(other));
    }

    [Fact]
    public void Trusting_another_folder_keeps_the_ones_already_trusted()
    {
        var other = Directory.CreateDirectory(Path.Combine(_directory, "other")).FullName;

        Store().Trust(_folder);
        Store().Trust(other);
        Store().Revoke(_folder);

        Assert.Equal([other], Store().TrustedFolders);
    }

    [Fact]
    public void A_change_that_cannot_be_staged_leaves_the_previous_list_in_place()
    {
        var other = Directory.CreateDirectory(Path.Combine(_directory, "other")).FullName;
        Store().Trust(other);
        var before = File.ReadAllText(_file);

        // A directory where the new list has to be staged, so the write fails after the old list was read.
        Directory.CreateDirectory(_file + ".new");

        Assert.True(Record.Exception(() => Store().Trust(_folder)) is IOException or UnauthorizedAccessException);
        Assert.True(Record.Exception(() => Store().Revoke(other)) is IOException or UnauthorizedAccessException);

        Assert.Equal(before, File.ReadAllText(_file));
        Assert.Equal([other], Store().TrustedFolders);
    }

    [Fact]
    public void A_change_interrupted_before_the_rename_leaves_the_previous_list_in_place()
    {
        var other = Directory.CreateDirectory(Path.Combine(_directory, "other")).FullName;
        Store().Trust(other);

        // What an update killed partway through its write leaves behind: the list itself is untouched.
        File.WriteAllText(_file + ".new", """{"trustedFolders":["pa""");

        Assert.Equal([other], Store().TrustedFolders);

        Store().Trust(_folder);
        Assert.Equal([other, _folder], Store().TrustedFolders);
    }

    [Fact]
    public void A_change_cannot_overwrite_one_another_process_is_making()
    {
        var other = Directory.CreateDirectory(Path.Combine(_directory, "other")).FullName;
        Store().Trust(other);

        // The lock another Relay holds for the length of its own update.
        using (new FileStream(_file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => Store().Trust(_folder));
            Assert.ThrowsAny<IOException>(() => Store().Revoke(other));
            Assert.Equal([other], Store().TrustedFolders);
        }

        Store().Trust(_folder);
        Assert.Equal([other, _folder], Store().TrustedFolders);
    }

    [Fact]
    public void A_finished_change_leaves_nothing_half_written_behind()
    {
        Store().Trust(_folder);
        Store().Revoke(_folder);

        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(_file)!, "*.new"));
    }

    [Fact]
    public void Without_a_configuration_folder_nothing_is_trusted_and_nothing_can_be_remembered()
    {
        var store = new WorkspaceTrustStore(filePath: null);

        Assert.False(store.IsTrusted(_folder));
        Assert.Empty(store.TrustedFolders);
        Assert.Throws<IOException>(() => store.Trust(_folder));
    }

    [Fact]
    public void Folder_names_are_compared_the_way_the_file_system_compares_them()
    {
        Store().Trust(_folder);

        Assert.Equal(OperatingSystem.IsWindows(), Store().IsTrusted(_folder.ToUpperInvariant()));
    }

    [Fact]
    public void Default_file_ignores_a_relative_RELAY_HOME_so_a_folder_cannot_supply_its_own_trust_file()
    {
        var original = Environment.GetEnvironmentVariable("RELAY_HOME");
        try
        {
            Environment.SetEnvironmentVariable("RELAY_HOME", ".");
            var fallback = Assert.IsType<string>(WorkspaceTrustStore.DefaultFilePath());
            Assert.True(Path.IsPathFullyQualified(fallback));
            Assert.DoesNotContain(Environment.CurrentDirectory, fallback);

            Environment.SetEnvironmentVariable("RELAY_HOME", _directory);
            Assert.Equal(Path.Combine(_directory, "trusted-folders.json"), WorkspaceTrustStore.DefaultFilePath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("RELAY_HOME", original);
        }
    }
}
