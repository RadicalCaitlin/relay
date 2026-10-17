using Microsoft.Extensions.DependencyInjection;
using Relay.Agents.Claude;
using Relay.Agents.Codex;
using Relay.Agents.Processes;
using Relay.Core.Agents;

namespace Relay.Agents;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the built-in providers. New providers are added here.</summary>
    /// <param name="agentWatcher">How to start <see cref="AgentWatcher"/>: this same Relay, with its command.</param>
    public static IServiceCollection AddRelayAgents(this IServiceCollection services, ProcessRequest agentWatcher)
        => services
            .AddSingleton<IExecutableLocator, PathExecutableLocator>()
            .AddSingleton<IProcessRunner>(new ProcessRunner(agentWatcher))
            .AddSingleton<IAgentProvider, ClaudeProvider>()
            .AddSingleton<IAgentProvider, CodexProvider>();
}
