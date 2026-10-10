using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Relay.Agents;
using Relay.Cli.Commands;
using Relay.Cli.Infrastructure;
using Relay.Core.Agents;
using Relay.Core.Rooms;
using Relay.Core.Routing;
using Spectre.Console;
using Spectre.Console.Cli;

Console.OutputEncoding = Encoding.UTF8;

var services = new ServiceCollection()
    .AddRelayAgents()
    .AddSingleton<AgentDiscoveryService>()
    .AddSingleton<IRoomManager, InMemoryRoomManager>()
    .AddSingleton<MessageRouter>()
    .AddSingleton(AnsiConsole.Console);

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

var app = new CommandApp<HomeCommand>(new TypeRegistrar(services));
app.Configure(config =>
{
    config.SetApplicationName("relay");
    config.SetApplicationVersion(version);
    config.AddCommand<AgentsCommand>("agents").WithDescription("List agent providers and whether they are installed.");
    config.AddCommand<StartCommand>("start").WithDescription("Start an interactive chatroom.");
});

return await app.RunAsync(args);
