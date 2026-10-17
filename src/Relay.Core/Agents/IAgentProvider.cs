namespace Relay.Core.Agents;

public interface IAgentProvider
{
    /// <summary>Lowercase handle used for <c>@mention</c> routing, e.g. <c>claude</c>.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <param name="includeVersion">Resolving the version may launch the agent, so it is opt-in.</param>
    Task<AgentDiscoveryResult> DiscoverAsync(bool includeVersion = false, CancellationToken cancellationToken = default);

    IAgentSession CreateSession(AgentDiscoveryResult discovery, AgentSessionOptions options);

    /// <summary>
    /// Whether this agent can put a step it is not allowed to take, or a question of its own, to the
    /// user through <see cref="AgentSessionOptions.ApprovalTool"/>. An agent that cannot is refused
    /// such a step by its own CLI, as before.
    /// </summary>
    bool CanAskForApproval => false;

    /// <summary>
    /// Whether this agent has a mode of its own in which it decides the steps its settings do not,
    /// which the user can switch it into with <see cref="AgentSessionOptions.AutoMode"/>.
    /// </summary>
    bool HasAutoMode => false;
}

/// <param name="WorkingDirectory">The directory the agent runs in.</param>
/// <param name="TrustWorkspace">
/// Whether the user has said they trust <paramref name="WorkingDirectory"/>. When false, a provider must not
/// let content in that directory configure the agent or run commands on its behalf.
/// </param>
/// <param name="ApprovalTool">
/// How an agent can ask the user before a step its own settings do not allow. Null when nobody can
/// be asked, and the agent's CLI then refuses such a step itself.
/// </param>
/// <param name="TurnTimeout">
/// How long one turn may run before the agent is stopped. Null for no limit.
/// </param>
/// <param name="AutoMode">
/// Asked when each turn starts: whether the user has switched the agent into its own auto mode.
/// Null, or false, leaves the agent's own settings to decide its mode.
/// </param>
public sealed record AgentSessionOptions(string WorkingDirectory, bool TrustWorkspace = false, ApprovalTool? ApprovalTool = null, TimeSpan? TurnTimeout = null, Func<bool>? AutoMode = null);

/// <summary>
/// A program that speaks the Model Context Protocol over its standard streams and offers one tool,
/// <see cref="ToolName"/>, which asks the user and returns their answer. Relay never answers for them.
/// </summary>
/// <param name="Command">The program to start. An absolute path.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="Directory">
/// A folder of Relay's own, outside the working directory, where a provider writes whatever its
/// CLI needs in order to find the tool.
/// </param>
public sealed record ApprovalTool(string Command, IReadOnlyList<string> Arguments, string Directory)
{
    public const string ServerName = "relay_approval";

    public const string ToolName = "ask";
}

public sealed record AgentDiscoveryResult(
    string ProviderId,
    string DisplayName,
    bool IsInstalled,
    string? ExecutablePath = null,
    string? Version = null,
    string? Detail = null);
