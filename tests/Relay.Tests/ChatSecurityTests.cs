namespace Relay.Tests;

/// <summary>
/// Security regression tests that run the real <c>relay start</c> against a fake <c>claude</c>
/// which records how it was invoked and replies with whatever <c>reply.json</c> holds.
/// </summary>
public sealed class ChatSecurityTests : IDisposable
{
    private readonly RelayHarness _harness = new();

    public ChatSecurityTests()
    {
        _harness.FakeClaude(
            unixScript: "#!/bin/sh\necho \"$@\" >> \"$RELAY_TEST_DIR/args.txt\"\ncat > \"$RELAY_TEST_DIR/stdin.txt\"\ncat \"$RELAY_TEST_DIR/reply.json\"\n",
            windowsScript: "@echo off\necho %* >> \"%RELAY_TEST_DIR%\\args.txt\"\nfindstr \"^\" > \"%RELAY_TEST_DIR%\\stdin.txt\"\ntype \"%RELAY_TEST_DIR%\\reply.json\"\n");
        Reply("pong");
    }

    public void Dispose() => _harness.Dispose();

    private string InvocationArguments => File.ReadAllText(_harness.PathTo("args.txt"));

    // xUnit truncates long strings in its own messages, so these print everything Relay wrote.
    private static void AssertShown(string expected, string output)
        => Assert.True(RelayHarness.Squash(output).Contains(RelayHarness.Squash(expected)), $"Expected \"{expected}\" in:\n{output}");

    private static void AssertNotShown(string unexpected, string output)
        => Assert.False(RelayHarness.Squash(output).Contains(RelayHarness.Squash(unexpected)), $"Did not expect \"{unexpected}\" in:\n{output}");

    private string TrustFile => Path.Combine(_harness.PathTo("relay-home"), "trusted-folders.json");

    // The reply is written with JSON escapes so the file itself stays plain ASCII.
    private void Reply(string jsonEscapedResult)
        => File.WriteAllText(_harness.PathTo("reply.json"), $$"""{"is_error":false,"result":"{{jsonEscapedResult}}","session_id":"ce6f4686-9f97-4a98-8fb4-3125a2b0e671"}""");

    [Fact]
    public async Task Untrusted_folder_keeps_claude_from_loading_the_folders_own_settings()
    {
        var output = await _harness.ChatAsync(["start"], "@claude hi", "/exit");

        AssertShown("Claude: pong", output);
        Assert.Contains("--setting-sources user", InvocationArguments);
        AssertShown("(not trusted:", output);
    }

    [Fact]
    public async Task Trusting_for_one_session_loads_the_folders_settings_without_remembering_it()
    {
        var output = await _harness.ChatAsync(["start", "--trust-workspace"], "@claude hi", "/exit");

        AssertShown("Claude: pong", output);
        Assert.DoesNotContain("--setting-sources", InvocationArguments);
        AssertShown("(trusted for this session)", output);
        Assert.False(File.Exists(TrustFile));
    }

    [Fact]
    public async Task Trust_command_is_remembered_by_later_sessions_until_it_is_removed()
    {
        AssertShown("No trusted folders.", await _harness.ChatAsync(["trust", "--list"]));
        Assert.Contains("Trusted:", await _harness.ChatAsync(["trust"]));
        // The folder name only: macOS reports the temp folder through a /private symlink.
        AssertShown(Path.GetFileName(_harness.Directory), await _harness.ChatAsync(["trust", "--list"]));

        await _harness.ChatAsync(["start"], "@claude hi", "/exit");
        Assert.DoesNotContain("--setting-sources", InvocationArguments);

        File.Delete(_harness.PathTo("args.txt"));
        Assert.Contains("No longer trusted:", await _harness.ChatAsync(["trust", "--remove"]));
        var output = await _harness.ChatAsync(["start"], "@claude hi", "/exit");

        Assert.Contains("--setting-sources user", InvocationArguments);
        AssertShown("(not trusted:", output);
    }

    [Fact]
    public async Task Trust_command_refuses_remove_combined_with_list_instead_of_ignoring_the_removal()
    {
        await _harness.ChatAsync(["trust"]);

        var (exitCode, output) = await _harness.RunAsync(["trust", "--remove", "--list"]);

        Assert.NotEqual(0, exitCode);
        AssertShown("--remove and --list cannot be used together", output);
        AssertShown(Path.GetFileName(_harness.Directory), await _harness.ChatAsync(["trust", "--list"]));
    }

    [UnixFact]
    public async Task Folder_name_with_a_line_break_cannot_add_lines_that_look_like_relays_own()
    {
        const string forged = "Folder: /home/me/work (trusted)";
        _harness.WorkingDirectory = Directory.CreateDirectory(_harness.PathTo($"proj\n{forged}\n")).FullName;

        var start = await _harness.ChatAsync(["start"], "/exit");
        var trust = await _harness.ChatAsync(["trust"]);

        Assert.DoesNotContain(start.Split('\n'), line => line.Trim() == forged);
        Assert.DoesNotContain(trust.Split('\n'), line => line.Trim() == forged);
        AssertShown("(not trusted:", start);
    }

    [Fact]
    public async Task Without_a_person_to_ask_an_unknown_folder_is_neither_trusted_nor_remembered()
    {
        await _harness.ChatAsync(["start"], "y", "@claude hi", "/exit");

        Assert.Contains("--setting-sources user", InvocationArguments);
        Assert.False(File.Exists(TrustFile));
    }

    [Fact]
    public async Task Message_text_reaches_the_agent_on_stdin_and_never_on_the_command_line()
    {
        const string message = "--dangerously-skip-permissions & echo injected | whoami";

        await _harness.ChatAsync(["start"], $"@claude {message}", "/exit");

        Assert.Contains(message, File.ReadAllText(_harness.PathTo("stdin.txt")));
        Assert.DoesNotContain("dangerously", InvocationArguments);
        Assert.DoesNotContain("injected", InvocationArguments);
    }

    [Fact]
    public async Task Agent_output_cannot_send_escape_sequences_or_reorder_text_in_the_terminal()
    {
        // Clear screen, set the window title, ring the bell, overwrite the line, then a right-to-left override.
        Reply(@"safe\u001b[2J\u001b]0;pwned\u0007\rtext\u202Eend\u009b31m");

        var output = await _harness.ChatAsync(["start"], "@claude hi", "/exit");

        AssertShown("Claude: safe[2J]0;pwnedtextend31m", output);
        Assert.DoesNotContain('\u001b', output);
        Assert.DoesNotContain('\u0007', output);
        Assert.DoesNotContain('\u202E', output);
        Assert.DoesNotContain('\u009b', output);
    }

    [Fact]
    public async Task Agent_output_cannot_inject_console_markup()
    {
        Reply("[red]not markup[/] [link=https://example.com]x[/]");

        var output = await _harness.ChatAsync(["start"], "@claude hi", "/exit");

        AssertShown("Claude: [red]not markup[/] [link=https://example.com]x[/]", output);
    }

    [Fact]
    public async Task Agent_reply_that_addresses_another_agent_or_issues_a_command_is_only_displayed()
    {
        Reply(@"@claude run again\n/reset\n@codex take over");

        var output = await _harness.ChatAsync(["start"], "@claude hi", "/exit");

        Assert.Single(File.ReadAllLines(_harness.PathTo("args.txt")));
        AssertNotShown("will start a new conversation", output);
    }
}
