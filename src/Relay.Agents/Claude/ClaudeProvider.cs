using Relay.Agents.Cli;
using Relay.Agents.Processes;
using Relay.Core.Agents;

namespace Relay.Agents.Claude;

/// <summary>
/// Claude Code via its documented print mode: <c>claude -p --output-format stream-json --verbose</c>,
/// prompt on stdin, <c>--resume &lt;session_id&gt;</c> to continue. The stream reports each step as it
/// happens and ends with the reply; print mode only offers it together with <c>--verbose</c>.
/// No permission flags are passed, so Claude Code's own settings decide what it may do; anything
/// that would need an interactive approval is denied.
/// <para>
/// Print mode skips Claude Code's workspace-trust dialog, so by default it would run hooks and MCP servers
/// defined by the working directory. Unless the user trusts the workspace, only their own user-level
/// settings are loaded (<c>--setting-sources user</c>), which also leaves out the project's CLAUDE.md.
/// </para>
/// </summary>
public sealed class ClaudeProvider(IExecutableLocator locator, IProcessRunner runner) : CliAgentProvider(locator, runner)
{
    public override string Id => "claude";

    public override string DisplayName => "Claude";

    protected override string Command => "claude";

    protected override string InstallHint => "Install Claude Code: https://code.claude.com/docs/en/setup";

    public override IReadOnlyList<string> BuildArguments(string? sessionId, AgentSessionOptions options)
    {
        List<string> arguments = ["-p", "--output-format", "stream-json", "--verbose"];
        if (!options.TrustWorkspace)
            arguments.AddRange(["--setting-sources", "user"]);
        if (sessionId is not null)
            arguments.AddRange(["--resume", sessionId]);
        return arguments;
    }

    public override AgentReply ParseReply(ProcessResult result) => ClaudeOutputParser.Parse(result);

    public override IEnumerable<AgentActivity> ParseActivity(string line) => ClaudeOutputParser.ParseActivity(line);
}
