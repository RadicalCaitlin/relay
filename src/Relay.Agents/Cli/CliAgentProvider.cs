using Relay.Agents.Processes;
using Relay.Core.Agents;

namespace Relay.Agents.Cli;

public sealed record AgentReply(string Text, string? SessionId);

/// <summary>
/// Base for providers that drive an installed agent CLI, one process per turn.
/// Subclasses supply only the documented non-interactive invocation and its output parser.
/// </summary>
public abstract class CliAgentProvider(IExecutableLocator locator, IProcessRunner runner) : IAgentProvider
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(15);

    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    protected abstract string Command { get; }

    protected abstract string InstallHint { get; }

    /// <summary>Arguments for one turn. The message itself is always written to stdin, never passed here.</summary>
    public abstract IReadOnlyList<string> BuildArguments(string? sessionId, AgentSessionOptions options);

    /// <exception cref="AgentException">The output does not contain a usable response.</exception>
    public abstract AgentReply ParseReply(ProcessResult result);

    public async Task<AgentDiscoveryResult> DiscoverAsync(bool includeVersion = false, CancellationToken cancellationToken = default)
    {
        if (locator.Find(Command) is not { } path)
            return new(Id, DisplayName, IsInstalled: false, Detail: $"'{Command}' was not found on PATH. {InstallHint}");

        var version = includeVersion ? await GetVersionAsync(path, cancellationToken) : null;
        return new(Id, DisplayName, IsInstalled: true, path, version);
    }

    public IAgentSession CreateSession(AgentDiscoveryResult discovery, AgentSessionOptions options)
    {
        if (discovery.ExecutablePath is not { } path)
            throw new InvalidOperationException($"{DisplayName} is not installed.");
        return new CliAgentSession(runner, path, options.WorkingDirectory, sessionId => BuildArguments(sessionId, options), ParseReply);
    }

    private async Task<string?> GetVersionAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync(new(path, ["--version"]) { Timeout = VersionTimeout }, cancellationToken);
            var firstLine = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return result.ExitCode == 0 ? firstLine : null;
        }
        catch (AgentException)
        {
            return null;
        }
    }
}
