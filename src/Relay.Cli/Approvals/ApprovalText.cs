using System.Text.Json;
using Relay.Cli.Ui;

namespace Relay.Cli.Approvals;

internal sealed record QuestionOption(string Label, string? Description);

internal sealed record Question(string Text, IReadOnlyList<QuestionOption> Options, bool Multiple);

/// <summary>
/// Turns what an agent's CLI wants to do into lines for the user to read before they answer.
/// All of it is the agent's own text, so every line is cleaned, starts with Relay's own indent,
/// and says so when part of a value is left out.
/// </summary>
internal static class ApprovalText
{
    // A command is what runs, so it is shown whole or the step is not offered at all.
    private const string CommandKey = "command";
    private const int MaxCommandLength = 4000;
    private const int MaxCommandLines = 60;

    private const int MaxFields = 20;
    private const int MaxLines = 12;
    private const int MaxLineLength = 300;
    private const int MaxNameLength = 40;

    /// <returns>Lines of console markup, or null when the step holds a command too long to show in full.</returns>
    public static IReadOnlyList<string>? Describe(JsonElement input)
    {
        List<string> lines = [];
        var fields = 0;
        foreach (var field in input.EnumerateObject())
        {
            if (++fields > MaxFields)
                continue;

            var whole = field.Name == CommandKey;
            var value = field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString()! : field.Value.GetRawText();
            var text = value.ReplaceLineEndings("\n").Replace("\t", "    ").Split('\n');
            if (whole && (value.Length > MaxCommandLength || text.Length > MaxCommandLines))
                return null;

            var name = Name(field.Name);
            if (text.Length == 1)
            {
                lines.Add($"      {name}: {(whole ? RelayConsole.SafeLine(text[0]) : Cut(text[0], MaxLineLength))}");
                continue;
            }

            var shown = whole ? text.Length : Math.Min(text.Length, MaxLines);
            lines.Add($"      {name}:");
            lines.AddRange(text.Take(shown).Select(line => $"      | {(whole ? RelayConsole.SafeLine(line) : Cut(line, MaxLineLength))}"));
            if (shown < text.Length)
                lines.Add($"      [yellow]| ... and {text.Length - shown} more lines that are not shown[/]");
        }

        if (fields > MaxFields)
            lines.Add($"      [yellow]... and {fields - MaxFields} more fields that are not shown[/]");
        return lines;
    }

    /// <summary>The name of a tool or of one of its fields, on one line and never long.</summary>
    public static string Name(string name) => Cut(name, MaxNameLength);

    /// <returns>The questions, or null when they do not have the shape Relay knows how to show.</returns>
    public static IReadOnlyList<Question>? ReadQuestions(JsonElement input, int maxQuestions, int maxOptions)
    {
        if (!input.TryGetProperty("questions", out var asked) || asked.ValueKind != JsonValueKind.Array
            || asked.GetArrayLength() is 0 || asked.GetArrayLength() > maxQuestions)
            return null;

        List<Question> questions = [];
        foreach (var item in asked.EnumerateArray())
        {
            if (String(item, "question") is not { Length: > 0 } text || questions.Any(q => q.Text == text))
                return null;

            List<QuestionOption> options = [];
            if (item.TryGetProperty("options", out var offered) && offered.ValueKind == JsonValueKind.Array)
            {
                if (offered.GetArrayLength() > maxOptions)
                    return null;
                foreach (var option in offered.EnumerateArray())
                {
                    if (String(option, "label") is not { Length: > 0 } label)
                        return null;
                    options.Add(new(label, String(option, "description")));
                }
            }

            questions.Add(new(text, options, item.TryGetProperty("multiSelect", out var multiple) && multiple.ValueKind == JsonValueKind.True));
        }

        return questions;
    }

    /// <summary>The options the numbers typed stand for, or what was typed when it is not numbers of options.</summary>
    public static string Choose(string typed, IReadOnlyList<QuestionOption> options, bool multiple)
    {
        var parts = typed.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length > 1 && !multiple)
            return typed;

        List<string> labels = [];
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var number) || number < 1 || number > options.Count)
                return typed;
            labels.Add(options[number - 1].Label);
        }

        return string.Join(", ", labels.Distinct());
    }

    private static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Cut(string text, int length)
    {
        if (text.Length <= length)
            return RelayConsole.SafeLine(text);

        // Half of a character written as a pair would be shown as a broken one.
        var cut = char.IsHighSurrogate(text[length - 1]) ? length - 1 : length;
        return $"{RelayConsole.SafeLine(text[..cut])}[yellow]... ({text.Length - cut} more characters not shown)[/]";
    }
}
