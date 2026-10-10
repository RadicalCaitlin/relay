using Relay.Agents.Cli;
using Relay.Agents.Processes;
using Relay.Core.Agents;

namespace Relay.Agents.Codex;

/// <summary>
/// OpenAI Codex via its documented non-interactive mode: <c>codex exec --json -</c>, prompt on stdin,
/// <c>codex exec resume &lt;thread_id&gt;</c> to continue. No sandbox or approval flags are passed, so Codex
/// keeps its own defaults, including its refusal to run outside a Git repository.
/// <para>
/// Workspace trust is left to Codex, which keeps its own trusted-directory and hook-trust checks in this
/// mode, so <see cref="AgentSessionOptions.TrustWorkspace"/> changes nothing here.
/// </para>
/// </summary>
public sealed class CodexProvider(IExecutableLocator locator, IProcessRunner runner) : CliAgentProvider(locator, runner)
{
    public override string Id => "codex";

    public override string DisplayName => "Codex";

    protected override string Command => "codex";

    protected override string InstallHint => "Install Codex: https://developers.openai.com/codex/cli";

    public override IReadOnlyList<string> BuildArguments(string? sessionId, AgentSessionOptions options)
        => sessionId is null
            ? ["exec", "--json", "-"]
            : ["exec", "resume", "--json", sessionId, "-"];

    public override AgentReply ParseReply(ProcessResult result) => CodexOutputParser.Parse(result);
}
