using System.Text.Json.Nodes;
using Relay.Agents.Claude;
using Relay.Agents.Processes;
using Relay.Cli.Approvals;
using Relay.Core.Agents;

namespace Relay.Tests;

/// <summary>
/// Runs the installed <c>claude</c>, with the arguments Relay gives it, to show that a step it may
/// not take reaches Relay and happens only when the answer is yes. It is the evidence for what
/// <c>docs/adr/0005-approvals.md</c> says Claude Code does; run it again when Claude Code is upgraded.
/// <para>
/// It uses the account Claude Code is signed in to, and two short turns of it. It runs in a folder
/// of its own with only the user's own settings, and expects those not to allow writing files
/// already: if they do, Claude Code never asks and the first assertion fails.
/// </para>
/// </summary>
public sealed class ClaudeApprovalLiveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("relay-claude-").FullName;
    private readonly string _folder;
    private readonly string _config;

    public ClaudeApprovalLiveTests()
    {
        _folder = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        _config = Directory.CreateDirectory(Path.Combine(_root, "config")).FullName;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [LiveClaudeFact]
    public async Task A_step_claude_may_not_take_is_put_to_relay_and_happens_only_on_a_yes()
    {
        List<string> asked = [];
        var allow = false;
        await using var server = new ApprovalServer(request =>
        {
            lock (asked)
                asked.Add(request.ToolName);
            return allow
                ? new JsonObject { ["behavior"] = "allow", ["updatedInput"] = JsonNode.Parse(request.Input.GetRawText()) }.ToJsonString()
                : ApprovalBridge.Refusal("The user said no to this step.");
        });
        server.Start();

        var locator = new PathExecutableLocator();
        var provider = new ClaudeProvider(locator, new ProcessRunner());
        var tool = new ApprovalTool(locator.Find("dotnet")!, [Path.Combine(AppContext.BaseDirectory, "relay.dll"), ApprovalBridge.Command, server.PipeName], _config);
        await using var session = provider.CreateSession(await provider.DiscoverAsync(), new(_folder, ApprovalTool: tool));

        await session.SendAsync("Use the Write tool to create a file named refused.txt containing the word hi. If you are not allowed to, do not try any other way; just say so.");

        lock (asked)
            Assert.Contains("Write", asked);
        Assert.False(File.Exists(Path.Combine(_folder, "refused.txt")));

        allow = true;
        await session.SendAsync("Now use the Write tool to create a file named allowed.txt containing the word hi.");

        Assert.True(File.Exists(Path.Combine(_folder, "allowed.txt")));
    }
}

/// <summary>A test that runs the real Claude Code CLI on the developer's own account, so it is opt-in.</summary>
public sealed class LiveClaudeFactAttribute : FactAttribute
{
    public const string Variable = "RELAY_LIVE_CLAUDE";

    public LiveClaudeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = $"Runs the installed Claude Code CLI on the account it is signed in to. Set {Variable}=1 to run it.";
    }
}
