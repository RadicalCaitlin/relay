using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Relay.Agents;
using Relay.Agents.Processes;
using Relay.Cli.Approvals;
using Relay.Cli.Commands;
using Relay.Cli.Infrastructure;
using Relay.Cli.Ui;
using Relay.Core.Agents;
using Relay.Core.Rooms;
using Relay.Core.Routing;
using Relay.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

// Started by an agent's CLI, not by a person. Its standard streams carry a protocol, so this runs
// before anything else could write to them.
if (args is [ApprovalBridge.Command, var pipe])
    return await ApprovalBridge.RunAsync(pipe, Console.OpenStandardInput(), Console.OpenStandardOutput());

// Started by Relay itself on macOS and Linux, to stop the agents if Relay is killed.
if (args is [AgentWatcher.Command, var relayId] && int.TryParse(relayId, out var relayProcessId))
    return await AgentWatcher.RunAsync(Console.OpenStandardInput(), relayProcessId);

// Started by Relay itself on macOS and Linux, to become an agent in a session of its own. The
// agent's standard streams are already these, so nothing may read or write them first.
if (args is [AgentWatcher.SessionCommand, _, ..])
    return AgentWatcher.RunInOwnSession(args[1..], Console.OpenStandardError());

Console.OutputEncoding = Encoding.UTF8;

var (relay, relayArguments) = RelayExecutable.With();
var services = new ServiceCollection()
    .AddRelayAgents(new ProcessRequest(relay, relayArguments))
    .AddSingleton<AgentDiscoveryService>()
    .AddSingleton<IRoomManager, InMemoryRoomManager>()
    .AddSingleton(new WorkspaceTrustStore(WorkspaceTrustStore.DefaultFilePath()))
    .AddSingleton<IConsoleStreams, ConsoleStreams>()
    .AddSingleton<WorkspaceTrustPrompt>()
    .AddSingleton(AnsiConsole.Console);

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

var app = new CommandApp<HomeCommand>(new TypeRegistrar(services));
app.Configure(config =>
{
    config.SetApplicationName("relay");
    config.SetApplicationVersion(version);
    // Reject options a command does not know, rather than silently ignoring a mistyped one.
    config.UseStrictParsing();
    config.AddCommand<AgentsCommand>("agents").WithDescription("List agent providers and whether they are installed.");
    config.AddCommand<StartCommand>("start").WithDescription("Start an interactive chatroom.");
    config.AddCommand<TrustCommand>("trust").WithDescription("Trust the current folder, so agents load its own settings.");
});

return await app.RunAsync(args);
