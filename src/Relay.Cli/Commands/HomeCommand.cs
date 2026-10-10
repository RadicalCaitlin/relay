using Relay.Cli.Ui;
using Relay.Core.Agents;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Relay.Cli.Commands;

internal sealed class HomeCommand(IAnsiConsole console, AgentDiscoveryService discovery) : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        console.WriteBanner();
        foreach (var (_, result) in await discovery.DiscoverAllAsync(cancellationToken: cancellationToken))
            console.WriteStatus(result.DisplayName, result.IsInstalled, result.IsInstalled ? "installed" : "not found");

        console.WriteLine();
        console.MarkupLine("  [bold]relay start[/]     Start an interactive chatroom");
        console.MarkupLine("  [bold]relay agents[/]    List agent providers and installation status");
        console.MarkupLine("  [bold]relay --help[/]    Show all commands and options");
        return 0;
    }
}
