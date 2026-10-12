using System.Text.Json;
using System.Text.Json.Nodes;
using Relay.Cli.Ui;
using Spectre.Console;

namespace Relay.Cli.Approvals;

/// <summary>
/// Puts what an agent's CLI asks to the user, and turns their answer into what that CLI is told.
/// Nothing is ever allowed here except by a yes the user typed after the question was shown:
/// not by an empty line, not by a line typed before it, and not when nobody can be asked.
/// </summary>
/// <param name="gate">Held while writing, so a question and a line of activity cannot run into each other.</param>
/// <param name="runningAgent">The name of the agent whose turn is in progress, or null when none is.</param>
/// <param name="readAnswer">The next line the user types, or null when the turn ended or was cancelled first.</param>
/// <param name="discardTypedAhead">Throws away what was typed before the question was shown.</param>
internal sealed class ApprovalDesk(IAnsiConsole console, Lock gate, Func<string?> runningAgent, Func<string?> readAnswer, Action discardTypedAhead)
{
    public const string QuestionTool = "AskUserQuestion";

    private const string NobodyToAsk = "Nobody is available to answer, so this was refused. Do not try it again; carry on without it and say what you could not do.";
    private const int MaxQuestions = 8;
    private const int MaxOptions = 10;

    // One question at a time: an agent can want several things at once, and there is one keyboard.
    private readonly Lock _asking = new();
    private volatile bool _enabled = true;

    /// <summary>When false, everything is refused without the user being asked, as if they were away.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Returns once no question is open.</summary>
    public void WaitUntilIdle()
    {
        lock (_asking)
        {
        }
    }

    public string Decide(ApprovalRequest request)
    {
        lock (_asking)
        {
            // A process an agent left behind, or one still closing, has nobody's turn to belong to.
            if (runningAgent() is not { } agent)
                return ApprovalBridge.Refusal(NobodyToAsk);

            var tool = ApprovalText.Name(request.ToolName);
            if (!_enabled)
            {
                Write($"[yellow]  ! {tool} was refused without asking you: approvals are off. /approvals on turns them on.[/]");
                return ApprovalBridge.Refusal(NobodyToAsk);
            }

            return request.ToolName == QuestionTool
                ? AskQuestions(Markup.Escape(agent), request.Input)
                : AskPermission(Markup.Escape(agent), tool, request.Input);
        }
    }

    private string AskPermission(string agent, string tool, JsonElement input)
    {
        if (ApprovalText.Describe(input) is not { } lines)
        {
            Write($"[yellow]  ! {tool} was refused without asking you: the command is too long to show in full.[/]");
            return ApprovalBridge.Refusal("The command is too long to show the user in full, so it was refused. Do it in smaller steps.");
        }

        discardTypedAhead();
        Write([$"[yellow]  ? {agent} wants to use {tool}:[/]", .. lines]);
        while (true)
        {
            Write("    Allow it this once? Type [bold]yes[/] or [bold]no[/]. Ctrl+C cancels the turn.");
            switch (Read()?.ToLowerInvariant())
            {
                case "y" or "yes":
                    return new JsonObject { ["behavior"] = "allow", ["updatedInput"] = JsonNode.Parse(input.GetRawText()) }.ToJsonString();
                case null or "n" or "no":
                    return ApprovalBridge.Refusal("The user said no to this step.");
                case "":
                    break;
                default:
                    Write("[yellow]    That was not sent to anyone.[/]");
                    break;
            }
        }
    }

    private string AskQuestions(string agent, JsonElement input)
    {
        if (ApprovalText.ReadQuestions(input, MaxQuestions, MaxOptions) is not { } questions)
        {
            Write($"[yellow]  ! {agent} asked something Relay could not show, so it was left unanswered.[/]");
            return ApprovalBridge.Refusal("Relay could not show the user that question. Ask it in your reply instead.");
        }

        discardTypedAhead();
        var answers = new JsonObject();
        for (var i = 0; i < questions.Count; i++)
        {
            var (text, options, multiple) = questions[i];
            var count = questions.Count > 1 ? $" ({i + 1} of {questions.Count})" : string.Empty;
            Write([
                $"[yellow]  ? {agent} asks{count}:[/] {RelayConsole.SafeLine(text)}",
                .. options.Select((option, n) => $"      {n + 1}. {RelayConsole.SafeLine(option.Label)}{(option.Description is { Length: > 0 } about && about != option.Label ? $" [dim]- {RelayConsole.SafeLine(about)}[/]" : string.Empty)}"),
            ]);

            string? answer = null;
            while (answer is null)
            {
                Write(options.Count == 0
                    ? "    Type your answer. /skip leaves it unanswered."
                    : $"    Type {(multiple ? "one or more numbers, separated by commas" : "a number")}, or an answer of your own. /skip leaves it unanswered.");
                switch (Read())
                {
                    case null or "/skip":
                        return ApprovalBridge.Refusal("The user chose not to answer. Carry on with your best judgement and say what you assumed.");
                    case "":
                        break;
                    case ['/', ..]:
                        Write("[yellow]    That is not an answer here, and it was not sent to anyone. Type /skip on its own to leave the question unanswered.[/]");
                        break;
                    case var typed:
                        answer = ApprovalText.Choose(typed, options, multiple);
                        break;
                }
            }

            answers[text] = answer;
        }

        var updated = new JsonObject { ["questions"] = JsonNode.Parse(input.GetProperty("questions").GetRawText()), ["answers"] = answers };
        return new JsonObject { ["behavior"] = "allow", ["updatedInput"] = updated }.ToJsonString();
    }

    private string? Read()
    {
        lock (gate)
            console.Markup("[bold green]You:[/] ");
        return readAnswer();
    }

    private void Write(params string[] lines)
    {
        lock (gate)
        {
            foreach (var line in lines)
                console.MarkupLine(line);
        }
    }
}
