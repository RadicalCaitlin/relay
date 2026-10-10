using Relay.Cli.Ui;
using Relay.Core.Agents;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Relay.Cli.Commands;

internal sealed class AgentsCommand(IAnsiConsole console, AgentDiscoveryService discovery) : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        console.WriteAgentTable(await discovery.DiscoverAllAsync(includeVersion: true, cancellationToken));
        return 0;
    }
}
