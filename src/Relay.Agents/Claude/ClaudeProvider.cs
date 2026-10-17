using System.Text.Json.Nodes;
using Relay.Agents.Cli;
using Relay.Agents.Processes;
using Relay.Core.Agents;

namespace Relay.Agents.Claude;

/// <summary>
/// Claude Code via its documented print mode: <c>claude -p --output-format stream-json --verbose</c>,
/// prompt on stdin, <c>--resume &lt;session_id&gt;</c> to continue. The stream reports each step as it
/// happens and ends with the reply; print mode only offers it together with <c>--verbose</c>.
/// No flag that approves anything is passed, so Claude Code's own settings decide what it may do. While
/// the user has switched it on, <c>--permission-mode auto</c> lets Claude Code's classifier decide the
/// steps those settings leave open (ADR 0006).
/// A step that would need an interactive approval is denied, unless the session has an
/// <see cref="ApprovalTool"/>: Claude Code is then told to put such a step to that tool
/// (<c>--mcp-config</c> and <c>--permission-prompt-tool</c>), which asks the user.
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

    public override bool CanAskForApproval => true;

    public override bool HasAutoMode => true;

    /// <summary>The name Claude Code gives the approval tool: <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>.</summary>
    public const string PermissionPromptTool = $"mcp__{ApprovalTool.ServerName}__{ApprovalTool.ToolName}";

    public override IReadOnlyList<string> BuildArguments(string? sessionId, AgentSessionOptions options)
    {
        List<string> arguments = ["-p", "--output-format", "stream-json", "--verbose"];
        if (!options.TrustWorkspace)
            arguments.AddRange(["--setting-sources", "user"]);
        // Only while the user has switched it on, and the only mode Relay ever passes (ADR 0006): auto
        // mode approves nothing by itself, keeps every deny and ask rule, and has the rest reviewed.
        if (options.AutoMode?.Invoke() == true)
            arguments.AddRange(["--permission-mode", "auto"]);
        // Naming a tool to ask approves nothing: Claude Code's own settings still decide every step
        // first, and what they would have refused for want of an answer is put to the user instead.
        if (options.ApprovalTool is { } tool)
            arguments.AddRange(["--mcp-config", McpConfigPath(tool), "--permission-prompt-tool", PermissionPromptTool]);
        if (sessionId is not null)
            arguments.AddRange(["--resume", sessionId]);
        return arguments;
    }

    public static string McpConfigPath(ApprovalTool tool) => Path.Combine(tool.Directory, "claude-mcp.json");

    // A file, because the same JSON as an argument would have to survive cmd.exe's quoting on Windows.
    protected override void Prepare(AgentSessionOptions options)
    {
        if (options.ApprovalTool is not { } tool)
            return;

        var server = new JsonObject { ["command"] = tool.Command, ["args"] = new JsonArray([.. tool.Arguments.Select(a => (JsonNode)a)]) };
        var config = new JsonObject { ["mcpServers"] = new JsonObject { [ApprovalTool.ServerName] = server } };
        File.WriteAllText(McpConfigPath(tool), config.ToJsonString());
    }

    public override AgentReply ParseReply(ProcessResult result) => ClaudeOutputParser.Parse(result);

    public override IEnumerable<AgentActivity> ParseActivity(string line) => ClaudeOutputParser.ParseActivity(line);
}
