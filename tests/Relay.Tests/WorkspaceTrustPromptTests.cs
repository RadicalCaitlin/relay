using Relay.Cli.Commands;
using Relay.Cli.Ui;
using Relay.Core.Workspaces;
using Spectre.Console.Testing;

namespace Relay.Tests;

/// <summary>
/// The question Relay asks about an unknown folder. These drive the real decision in process,
/// because every test that runs the built CLI redirects its streams and so can never reach it.
/// </summary>
public sealed class WorkspaceTrustPromptTests : IDisposable
{
    private const string Question = "Do you trust the files in this folder?";

    private static readonly Streams NothingRedirected = new(false, false);

    private readonly string _directory = Directory.CreateTempSubdirectory("relay-prompt-").FullName;
    private readonly string _file;
    private readonly string _folder;

    public WorkspaceTrustPromptTests()
    {
        _file = Path.Combine(_directory, "config", "trusted-folders.json");
        _folder = Directory.CreateDirectory(Path.Combine(_directory, "repo")).FullName;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed record Streams(bool IsInputRedirected, bool IsOutputRedirected) : IConsoleStreams;

    private static TestConsole Terminal(string answer)
    {
        var console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter(answer);
        return console;
    }

    private WorkspaceTrustDecision Resolve(TestConsole console, Streams streams, WorkspaceTrustStore? store = null)
        => new WorkspaceTrustPrompt(console, store ?? new WorkspaceTrustStore(_file), streams).Resolve(trustWorkspace: false, _folder);

    [Fact]
    public void Yes_trusts_the_folder_and_remembers_it()
    {
        var console = Terminal("y");

        var decision = Resolve(console, NothingRedirected);

        Assert.True(decision.Trusted);
        Assert.Contains("(trusted)", decision.Status);
        Assert.Contains(Question, console.Output);
        // A later run reads the answer back from the user's own configuration folder.
        Assert.True(new WorkspaceTrustStore(_file).IsTrusted(_folder));
    }

    [Fact]
    public void Yes_that_cannot_be_written_down_says_so_and_trusts_the_folder_for_this_session_only()
    {
        var console = Terminal("y");

        // No configuration folder to write to: the choice cannot outlive this run.
        var decision = Resolve(console, NothingRedirected, new WorkspaceTrustStore(filePath: null));

        Assert.True(decision.Trusted);
        Assert.Contains("(trusted for this session)", decision.Status);
        Assert.Contains("Could not remember this choice", console.Output);
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public void No_leaves_the_folder_untrusted_and_writes_nothing_down()
    {
        var console = Terminal("n");

        var decision = Resolve(console, NothingRedirected);

        Assert.False(decision.Trusted);
        Assert.Contains("(not trusted:", decision.Status);
        Assert.Contains(Question, console.Output);
        Assert.False(File.Exists(_file));
        Assert.False(new WorkspaceTrustStore(_file).IsTrusted(_folder));
    }

    [Fact]
    public void No_is_not_remembered_either_so_the_next_run_asks_again()
    {
        Resolve(Terminal("n"), NothingRedirected);

        var console = Terminal("n");
        var again = Resolve(console, NothingRedirected);

        Assert.False(again.Trusted);
        Assert.Contains(Question, console.Output);
    }

    [Fact]
    public void Simply_pressing_enter_does_not_trust_the_folder()
    {
        var console = Terminal(string.Empty);

        var decision = Resolve(console, NothingRedirected);

        Assert.False(decision.Trusted);
        Assert.False(File.Exists(_file));
    }

    // Each condition on its own, with the other two allowing the question, so dropping any one of
    // the three checks fails a test here.
    [Theory]
    [InlineData(true, false, true)]   // relay start < answers.txt
    [InlineData(false, true, true)]   // relay start > log.txt
    [InlineData(false, false, false)] // a terminal the console reports it cannot interact with
    public void Without_a_person_to_ask_the_question_is_skipped_and_the_folder_stays_untrusted(
        bool inputRedirected, bool outputRedirected, bool interactive)
    {
        var console = new TestConsole();
        if (interactive)
            console.Interactive();
        // A yes that must never be read: it is only there to fail this test if the question is asked.
        console.Input.PushTextWithEnter("y");

        var decision = Resolve(console, new Streams(inputRedirected, outputRedirected));

        Assert.False(decision.Trusted);
        Assert.Contains("(not trusted:", decision.Status);
        Assert.DoesNotContain(Question, console.Output);
        Assert.False(File.Exists(_file));
        Assert.False(new WorkspaceTrustStore(_file).IsTrusted(_folder));
    }

    [Fact]
    public void A_folder_already_remembered_is_trusted_without_asking()
    {
        new WorkspaceTrustStore(_file).Trust(_folder);
        var console = Terminal("n");

        var decision = Resolve(console, NothingRedirected);

        Assert.True(decision.Trusted);
        Assert.DoesNotContain(Question, console.Output);
    }
}
