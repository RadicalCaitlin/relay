using System.Text;
using System.Text.RegularExpressions;
using Relay.Core.Rooms;
using Spectre.Console;

namespace Relay.Cli.Ui;

/// <summary>
/// Shows a message with the common parts of Markdown rendered in Relay's own styles (ADR 0007).
/// The text is cleaned of control characters, split into spans, and each span is escaped on its
/// own before it is wrapped in one of the fixed styles below, so the only markup in the result is
/// Relay's: the text can choose which of these styles a span gets, and nothing else.
/// </summary>
internal static partial class ReplyRenderer
{
    internal const string MentionStyle = "bold aqua";
    internal const string CodeStyle = "teal";

    /// <summary>Every style the renderer can produce. A test checks that nothing else ever appears.</summary>
    internal static readonly string[] Styles = [MentionStyle, CodeStyle, "bold", "bold underline", "italic", "dim"];

    // Spans are looked for by scanning ahead for a closing marker, which on a line made mostly of
    // unclosed markers costs time in proportion to the square of its length. Longer lines are shown as written.
    private const int MaxRenderedLine = 4000;

    /// <summary>The message as Spectre markup, with the leading mentions the router acted on highlighted.</summary>
    // The router counted characters of the text as written; cleaning removes characters one at a
    // time, so the cleaned address is the start of the cleaned text.
    public static string Message(ChatMessage message, bool unicode)
        => Render(message.Content, RelayConsole.Clean(message.Content[..message.AddressLength]).Length, unicode);

    /// <param name="highlighted">How many characters at the start are mentions Relay acted on.</param>
    public static string Render(string text, int highlighted = 0, bool unicode = true)
    {
        // Clean removes '\r' with the other control characters, so a "\r\n" ending is already "\n".
        var lines = RelayConsole.Clean(text).Split('\n');
        var output = new List<string>(lines.Length);
        string? fence = null;
        var bar = unicode ? "│" : "|";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            var indent = Markup.Escape(line[..(line.Length - trimmed.Length)]);

            if (fence is not null)
            {
                // Only a fence at least as long as the one that opened the block, with nothing after
                // it, closes it: a block can hold a shorter fence, as when it shows another block.
                if (FenceClose().Match(trimmed) is { Success: true } close && close.Groups[1].Length >= fence.Length)
                    fence = null;
                else
                    output.Add(line.Length == 0 ? $"[dim]{bar}[/]" : $"[dim]{bar}[/] [{CodeStyle}]{Markup.Escape(line)}[/]");
                continue;
            }

            // A backtick after the opening fence (```code```) makes it inline code, not a block.
            if (FenceOpen().Match(trimmed) is { Success: true } open && !open.Groups[2].Value.Contains('`'))
            {
                fence = open.Groups[1].Value;
                if (open.Groups[2].Value.Trim() is { Length: > 0 } language)
                    output.Add($"{indent}[dim]{Markup.Escape(language)}[/]");
                continue;
            }

            if (i == 0 && highlighted > 0)
            {
                output.Add($"[{MentionStyle}]{Markup.Escape(line[..highlighted])}[/]{Inline(line[highlighted..])}");
                continue;
            }

            output.Add(Block(trimmed, indent, unicode, bar) ?? Inline(line));
        }

        return string.Join('\n', output);
    }

    private static string? Block(string trimmed, string indent, bool unicode, string bar)
    {
        if (Heading().Match(trimmed) is { Success: true } heading)
            return $"{indent}[{(heading.Groups[1].Length <= 2 ? "bold underline" : "bold")}]{Inline(HeadingText(heading.Groups[2].Value))}[/]";
        // A rule such as "* * *" is shown as written, not as a bullet.
        if (Rule().IsMatch(trimmed))
            return $"{indent}{Markup.Escape(trimmed)}";
        if (Bullet().Match(trimmed) is { Success: true } bullet)
            return $"{indent}{(unicode ? "•" : "-")} {Inline(bullet.Groups[1].Value)}";
        if (Numbered().Match(trimmed) is { Success: true } numbered)
            return $"{indent}{Markup.Escape(numbered.Groups[1].Value)} {Inline(numbered.Groups[2].Value)}";
        if (Quote().Match(trimmed) is { Success: true } quote)
            return $"{indent}[dim]{bar} {Inline(quote.Groups[1].Value)}[/]";
        return null;
    }

    // Closing hashes count only after a space, so "Using C#" keeps its own. They are found here
    // rather than in the pattern, where a long run of spaces made matching quadratic.
    private static string HeadingText(string text)
    {
        var title = text.TrimEnd();
        var withoutHashes = title.TrimEnd('#');
        return withoutHashes.Length < title.Length && withoutHashes.TrimEnd() is { Length: > 0 } kept && kept.Length < withoutHashes.Length
            ? kept
            : title;
    }

    private static string Inline(string text)
    {
        if (text.Length > MaxRenderedLine)
            return Markup.Escape(text);

        var output = new StringBuilder();
        var plain = new StringBuilder();
        void Flush()
        {
            output.Append(Markup.Escape(plain.ToString()));
            plain.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                var run = CodeSpan(text, i, out var close);
                if (close >= 0)
                {
                    Flush();
                    output.Append($"[{CodeStyle}]{Markup.Escape(text[(i + run)..close])}[/]");
                    i = close + run;
                    continue;
                }

                plain.Append('`', run);
                i += run;
                continue;
            }

            if (text[i] == '*')
            {
                var marker = text.AsSpan(i).StartsWith("***") ? "***" : text.AsSpan(i).StartsWith("**") ? "**" : "*";
                var start = i + marker.Length;
                if (CanOpen(text, i, marker.Length) && FindClose(text, start, marker) is var close and >= 0)
                {
                    Flush();
                    var inner = Inline(text[start..close]);
                    output.Append(marker switch
                    {
                        "***" => $"[bold][italic]{inner}[/][/]",
                        "**" => $"[bold]{inner}[/]",
                        _ => $"[italic]{inner}[/]",
                    });
                    i = close + marker.Length;
                    continue;
                }

                plain.Append(marker);
                i += marker.Length;
                continue;
            }

            plain.Append(text[i]);
            i++;
        }

        Flush();
        return output.ToString();
    }

    // Emphasis opens only at the start of a word, so a glob such as **/*.cs or a product a*b*c is
    // left alone: what comes before is nothing, a space or an opening bracket or quote, and what
    // comes after is the text itself.
    private static bool CanOpen(string text, int at, int length)
        => (at == 0 || char.IsWhiteSpace(text[at - 1]) || "([{\"'".Contains(text[at - 1]))
            && at + length < text.Length && !char.IsWhiteSpace(text[at + length]) && text[at + length] != '*';

    // The first marker that can close: it follows the text itself and ends a word. A code span is
    // passed over whole, so a marker inside one never closes emphasis around it.
    private static int FindClose(string text, int from, string marker)
    {
        var j = from;
        while (j < text.Length)
        {
            if (text[j] == '`')
            {
                var run = CodeSpan(text, j, out var end);
                j = end >= 0 ? end + run : j + run;
                continue;
            }

            var after = j + marker.Length;
            if (j > from && text.AsSpan(j).StartsWith(marker) && !char.IsWhiteSpace(text[j - 1])
                && (after == text.Length || char.IsWhiteSpace(text[after]) || ".,;:!?)]}\"'".Contains(text[after]))
                && (marker.Length > 1 || text[j - 1] != '*'))
                return j;

            j++;
        }

        return -1;
    }

    // The run of backticks at `at`, and where the run that closes it begins, or -1. As in
    // CommonMark, only a run of exactly the same length closes it, so a span can hold a shorter or
    // longer one. Inline and FindClose both use this, so emphasis and code always agree on where a
    // span ends.
    private static int CodeSpan(string text, int at, out int close)
    {
        var run = Run(text, at);
        for (var j = at + run; j < text.Length;)
        {
            if (text[j] != '`')
            {
                j++;
                continue;
            }

            var length = Run(text, j);
            if (length == run)
            {
                close = j;
                return run;
            }

            j += length;
        }

        close = -1;
        return run;
    }

    private static int Run(string text, int at)
    {
        var end = at;
        while (end < text.Length && text[end] == '`')
            end++;
        return end - at;
    }

    [GeneratedRegex(@"^(`{3,})(.*)$")]
    private static partial Regex FenceOpen();

    [GeneratedRegex(@"^(`{3,})[ \t]*$")]
    private static partial Regex FenceClose();

    [GeneratedRegex(@"^(#{1,6})[ \t]+(.+)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^([-*_])[ \t]*(?:\1[ \t]*){2,}$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^[-*+][ \t]+(.*)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^(\d{1,9}[.)])[ \t]+(.*)$")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"^>[ \t]?(.*)$")]
    private static partial Regex Quote();
}
