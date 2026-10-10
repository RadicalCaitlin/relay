namespace Relay.Core.Agents;

public sealed record DiscoveredAgent(IAgentProvider Provider, AgentDiscoveryResult Result);

public sealed class AgentDiscoveryService(IEnumerable<IAgentProvider> providers)
{
    public async Task<IReadOnlyList<DiscoveredAgent>> DiscoverAllAsync(CancellationToken cancellationToken = default)
        => await Task.WhenAll(providers.Select(p => DiscoverAsync(p, cancellationToken)));

    private static async Task<DiscoveredAgent> DiscoverAsync(IAgentProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            return new(provider, await provider.DiscoverAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One broken provider must not hide the others.
            return new(provider, new(provider.Id, provider.DisplayName, IsInstalled: false, Detail: $"Discovery failed: {ex.Message}"));
        }
    }
}
