using Relay.Cli.Ui;
using Relay.Core.Workspaces;
using Spectre.Console;

namespace Relay.Cli.Commands;

/// <summary>Whether agents may load the folder's own settings, and what the chatroom header says about it.</summary>
internal sealed record WorkspaceTrustDecision(bool Trusted, string Status);

/// <summary>
/// The agent CLIs skip their own trust prompt when run non-interactively, so Relay keeps its own
/// record. A folder is untrusted until the user says otherwise, and a yes is remembered.
/// </summary>
internal sealed class WorkspaceTrustPrompt(IAnsiConsole console, WorkspaceTrustStore trustStore, IConsoleStreams streams)
{
    public WorkspaceTrustDecision Resolve(bool trustWorkspace, string folder)
    {
        var name = RelayConsole.SafeLine(folder);
        if (trustWorkspace)
            return new WorkspaceTrustDecision(true, $"[dim]Folder: {name} (trusted for this session)[/]");

        if (IsRemembered(folder))
            return new WorkspaceTrustDecision(true, $"[dim]Folder: {name} (trusted)[/]");

        if (AskToTrust(name))
            return new WorkspaceTrustDecision(true, Remember(folder)
                ? $"[dim]Folder: {name} (trusted)[/]"
                : $"[dim]Folder: {name} (trusted for this session)[/]");

        return new WorkspaceTrustDecision(false, $"[yellow]Folder: {name} (not trusted: Claude ignores its settings, hooks, MCP servers and CLAUDE.md, and Codex applies its own trust checks; run 'relay trust' in it to change that)[/]");
    }

    private bool IsRemembered(string folder)
    {
        try
        {
            return trustStore.IsTrusted(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            console.MarkupLine($"[yellow]Could not read the trusted-folder list: {RelayConsole.SafeLine(ex.Message)}[/]");
            return false;
        }
    }

    // Without a person who can both see the question and answer it, the folder stays untrusted.
    private bool AskToTrust(string name)
    {
        if (streams.IsInputRedirected || streams.IsOutputRedirected || !console.Profile.Capabilities.Interactive)
            return false;

        console.MarkupLine($"Agents will run in [bold]{name}[/].");
        console.MarkupLine("[dim]A trusted folder can configure Claude: its hooks and MCP servers run commands on your machine. Codex applies its own trust checks either way.[/]");
        var trusted = console.Confirm("Do you trust the files in this folder?", defaultValue: false);
        console.WriteLine();
        return trusted;
    }

    private bool Remember(string folder)
    {
        try
        {
            trustStore.Trust(folder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            console.MarkupLine($"[yellow]Could not remember this choice: {RelayConsole.SafeLine(ex.Message)}[/]");
            return false;
        }
    }
}
