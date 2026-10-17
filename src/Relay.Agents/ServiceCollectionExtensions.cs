using Microsoft.Extensions.DependencyInjection;
using Relay.Agents.Claude;
using Relay.Agents.Codex;
using Relay.Agents.Processes;
using Relay.Core.Agents;

namespace Relay.Agents;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the built-in providers. New providers are added here.</summary>
    /// <param name="relay">How to start this same Relay, which keeps the agents from outliving it on macOS and Linux.</param>
    public static IServiceCollection AddRelayAgents(this IServiceCollection services, ProcessRequest relay)
        => services
            .AddSingleton<IExecutableLocator, PathExecutableLocator>()
            .AddSingleton<IProcessRunner>(new ProcessRunner(relay))
            .AddSingleton<IAgentProvider, ClaudeProvider>()
            .AddSingleton<IAgentProvider, CodexProvider>();
}
