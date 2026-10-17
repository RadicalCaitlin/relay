using System.Text;
using System.Text.RegularExpressions;
using Relay.Core.Rooms;
using Relay.Core.Routing;
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

    /// <summary>The message as Spectre markup, with the leading mentions Relay read as an address highlighted.</summary>
    public static string Message(ChatMessage message, ChatRoom room, bool unicode)
    {
        var text = RelayConsole.Clean(message.Content);
        return Render(text, AddressLength(text, message, room), unicode);
    }

    /// <param name="highlighted">How many characters at the start are mentions Relay acted on.</param>
    public static string Render(string text, int highlighted = 0, bool unicode = true)
    {
        var lines = RelayConsole.Clean(text).Replace("\r\n", "\n").Split('\n');
        var output = new List<string>(lines.Length);
        var inFence = false;
        var bar = unicode ? "│" : "|";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            var indent = Markup.Escape(line[..(line.Length - trimmed.Length)]);

            // A line that also closes the fence (```code```) is inline code, not the start of a block.
            if (trimmed.StartsWith("```", StringComparison.Ordinal) && (inFence || trimmed.IndexOf("```", 3, StringComparison.Ordinal) < 0))
            {
                inFence = !inFence;
                var language = trimmed.TrimStart('`').Trim();
                if (inFence && language.Length > 0)
                    output.Add($"{indent}[dim]{Markup.Escape(language)}[/]");
                continue;
            }

            if (inFence)
            {
                output.Add(line.Length == 0 ? $"[dim]{bar}[/]" : $"[dim]{bar}[/] [{CodeStyle}]{Markup.Escape(line)}[/]");
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
            return $"{indent}[{(heading.Groups[1].Length <= 2 ? "bold underline" : "bold")}]{Inline(heading.Groups[2].Value)}[/]";
        if (Bullet().Match(trimmed) is { Success: true } bullet)
            return $"{indent}{(unicode ? "•" : "-")} {Inline(bullet.Groups[1].Value)}";
        if (Numbered().Match(trimmed) is { Success: true } numbered)
            return $"{indent}{Markup.Escape(numbered.Groups[1].Value)} {Inline(numbered.Groups[2].Value)}";
        if (Quote().Match(trimmed) is { Success: true } quote)
            return $"{indent}[dim]{bar} {Inline(quote.Groups[1].Value)}[/]";
        return null;
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
                var run = 1;
                while (i + run < text.Length && text[i + run] == '`')
                    run++;
                var fence = new string('`', run);
                var close = text.IndexOf(fence, i + run, StringComparison.Ordinal);
                if (close > i + run)
                {
                    Flush();
                    output.Append($"[{CodeStyle}]{Markup.Escape(text[(i + run)..close])}[/]");
                    i = close + run;
                    continue;
                }

                plain.Append(fence);
                i += run;
                continue;
            }

            if (text[i] == '*')
            {
                var marker = i + 1 < text.Length && text[i + 1] == '*' ? "**" : "*";
                var start = i + marker.Length;
                var close = text.IndexOf(marker, start, StringComparison.Ordinal);
                // Closed on the same line, not empty, and not "a * b * c": the text inside touches both markers.
                if (close > start && !char.IsWhiteSpace(text[start]) && !char.IsWhiteSpace(text[close - 1]))
                {
                    Flush();
                    output.Append($"[{(marker == "**" ? "bold" : "italic")}]{Inline(text[start..close])}[/]");
                    i = close + marker.Length;
                    continue;
                }

                plain.Append(text[i]);
                i++;
                continue;
            }

            plain.Append(text[i]);
            i++;
        }

        Flush();
        return output.ToString();
    }

    // The mentions at the start that Relay acted on: an agent's handover, recorded as addressed to
    // an agent in the room, or the leading mentions of a human message that name agents in the room.
    // Anything else, here or later in the text, is shown as written.
    private static int AddressLength(string text, ChatMessage message, ChatRoom room)
    {
        var firstLine = text.Split('\n', 2)[0];
        switch (message.SenderType)
        {
            // The router records a reply as addressed to an agent only when it read a handover to it.
            case SenderType.Agent when message.TargetId is { } target && Mention.TryParseHandover(text, out var named, out _) && named == target:
                return firstLine.IndexOf('@') + 1 + named.Length;
            case SenderType.Human:
                var rest = firstLine;
                while (Mention.TryParse(rest, out var next, out _) && room.FindParticipant(next) is not null)
                {
                    var at = rest.IndexOf('@') + 1 + next.Length;
                    rest = rest[at..];
                }

                return firstLine.Length - rest.Length;
            default:
                return 0;
        }
    }

    // Closing hashes count only after a space, so "Using C#" keeps its own.
    [GeneratedRegex(@"^(#{1,6})[ \t]+(.+?)(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^[-*+][ \t]+(.*)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^(\d{1,9}[.)])[ \t]+(.*)$")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"^>[ \t]?(.*)$")]
    private static partial Regex Quote();
}
