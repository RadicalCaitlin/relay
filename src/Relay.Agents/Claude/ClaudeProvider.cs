using Relay.Agents.Cli;
using Relay.Agents.Processes;

namespace Relay.Agents.Claude;

/// <summary>
/// Claude Code via its documented print mode: <c>claude -p --output-format json</c>, prompt on stdin,
/// <c>--resume &lt;session_id&gt;</c> to continue. No permission flags are passed, so Claude Code's own
/// settings decide what it may do; anything that would need an interactive approval is denied.
/// </summary>
public sealed class ClaudeProvider(IExecutableLocator locator, IProcessRunner runner) : CliAgentProvider(locator, runner)
{
    public override string Id => "claude";

    public override string DisplayName => "Claude";

    protected override string Command => "claude";

    protected override string InstallHint => "Install Claude Code: https://code.claude.com/docs/en/setup";

    public override IReadOnlyList<string> BuildArguments(string? sessionId)
        => sessionId is null
            ? ["-p", "--output-format", "json"]
            : ["-p", "--output-format", "json", "--resume", sessionId];

    public override AgentReply ParseReply(ProcessResult result) => ClaudeOutputParser.Parse(result);
}
