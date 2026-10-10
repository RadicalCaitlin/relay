using Microsoft.Extensions.DependencyInjection;
using Relay.Agents.Claude;
using Relay.Agents.Codex;
using Relay.Agents.Processes;
using Relay.Core.Agents;

namespace Relay.Agents;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the built-in providers. New providers are added here.</summary>
    public static IServiceCollection AddRelayAgents(this IServiceCollection services)
        => services
            .AddSingleton<IExecutableLocator, PathExecutableLocator>()
            .AddSingleton<IProcessRunner, ProcessRunner>()
            .AddSingleton<IAgentProvider, ClaudeProvider>()
            .AddSingleton<IAgentProvider, CodexProvider>();
}
