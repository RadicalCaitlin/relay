using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Relay.Agents;
using Relay.Cli.Commands;
using Relay.Cli.Infrastructure;
using Relay.Core.Agents;
using Relay.Core.Rooms;
using Relay.Core.Routing;
using Relay.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

Console.OutputEncoding = Encoding.UTF8;

var services = new ServiceCollection()
    .AddRelayAgents()
    .AddSingleton<AgentDiscoveryService>()
    .AddSingleton<IRoomManager, InMemoryRoomManager>()
    .AddSingleton<MessageRouter>()
    .AddSingleton(new WorkspaceTrustStore(WorkspaceTrustStore.DefaultFilePath()))
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
