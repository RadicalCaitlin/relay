using System.ComponentModel;
using Relay.Cli.Ui;
using Relay.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Relay.Cli.Commands;

internal sealed class TrustCommand(IAnsiConsole console, WorkspaceTrustStore trustStore) : Command<TrustCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandOption("--remove")]
        [Description("Stop trusting the current folder.")]
        public bool Remove { get; init; }

        [CommandOption("--list")]
        [Description("List every trusted folder.")]
        public bool List { get; init; }

        public override ValidationResult Validate()
            => Remove && List
                ? ValidationResult.Error("--remove and --list cannot be used together.")
                : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var folder = RelayConsole.SafeLine(Environment.CurrentDirectory);
        try
        {
            if (settings.List)
            {
                var folders = trustStore.TrustedFolders;
                foreach (var trusted in folders)
                    console.MarkupLine(RelayConsole.SafeLine(trusted));
                if (folders.Count == 0)
                    console.MarkupLine("[dim]No trusted folders.[/]");
            }
            else if (settings.Remove)
            {
                console.MarkupLine(trustStore.Revoke(Environment.CurrentDirectory)
                    ? $"No longer trusted: {folder}"
                    : $"[dim]Not a trusted folder: {folder}[/]");
            }
            else
            {
                trustStore.Trust(Environment.CurrentDirectory);
                console.MarkupLine($"Trusted: {folder}");
                console.MarkupLine("[dim]Agents started here will load this folder's own settings, hooks and MCP servers.[/]");
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            console.MarkupLine($"[red]Could not use the trusted-folder list: {RelayConsole.SafeLine(ex.Message)}[/]");
            return 1;
        }
    }
}
