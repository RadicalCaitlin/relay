using Relay.Core.Agents;
using Spectre.Console;

namespace Relay.Cli.Ui;

internal static class RelayConsole
{
    /// <summary>
    /// Makes text that Relay did not write (agent replies, CLI errors, version strings) safe to print:
    /// removes control characters, so it cannot send escape sequences to the terminal, and bidirectional
    /// overrides, so it cannot reorder what is shown. Then escapes Spectre markup.
    /// </summary>
    public static string Safe(string text)
        => Markup.Escape(string.Concat(text.Where(c => c is '\n' or '\t' || !IsUnsafe(c))));

    /// <summary>
    /// As <see cref="Safe"/>, for text that must stay on one line, such as a folder name. A name
    /// containing a line break could otherwise add lines that look like Relay's own output.
    /// </summary>
    public static string SafeLine(string text)
        => Markup.Escape(string.Concat(text.Where(c => !IsUnsafe(c))));

    /// <summary>
    /// One safe line describing a step an agent took: line breaks become spaces, paths under
    /// <paramref name="folder"/> are shown relative to it, and anything long is cut short, because a
    /// command or a note can be any length.
    /// </summary>
    public static string ActivityLine(string text, string folder)
    {
        const int maxLength = 200;
        var line = WithoutFolder(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), folder);
        if (line.Length <= maxLength)
            return SafeLine(line);

        return SafeLine(line[..CutAt(line, maxLength)] + "...");
    }

    /// <summary>Where to cut text longer than <paramref name="maxLength"/>: half of a character written as a pair would be shown as a broken one.</summary>
    public static int CutAt(string text, int maxLength)
        => char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;

    // Removes the folder only where a path starts with it. Inside a longer path the same text is
    // some other folder: in /app, /usr/share/app/config.yml is not under the working folder.
    private static string WithoutFolder(string line, string folder)
    {
        var prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var at = 0;
        while ((at = line.IndexOf(prefix, at, comparison)) >= 0)
        {
            if (at == 0 || !IsPathCharacter(line[at - 1]))
                line = line.Remove(at, prefix.Length);
            else
                at++;
        }

        return line;
    }

    private static bool IsPathCharacter(char c) => char.IsLetterOrDigit(c) || c is '/' or '\\' or '.' or '-' or '_' or '~' or ':';

    private static bool IsUnsafe(char c) => char.IsControl(c) || IsBidiControl(c);

    // Written as escapes on purpose: the characters themselves are invisible in an editor or a diff.
    private static bool IsBidiControl(char c)
        => c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or '\u200E' or '\u200F' or '\u061C';

    public static void WriteBanner(this IAnsiConsole console)
    {
        var title = new Rows(
            new Markup("[bold aqua]RELAY[/]").Centered(),
            new Markup("[dim]Your agents. One conversation.[/]").Centered());
        console.Write(new Panel(title) { Border = BoxBorder.Rounded, Padding = new Padding(4, 0) });
        console.WriteLine();
    }

    public static void WriteStatus(this IAnsiConsole console, string name, bool available, string label)
    {
        var unicode = console.Profile.Capabilities.Unicode;
        var bullet = available ? $"[green]{(unicode ? "●" : "*")}[/]" : $"[dim]{(unicode ? "○" : "-")}[/]";
        var style = available ? "default" : "dim";
        console.MarkupLine($"{bullet} [{style}]{Markup.Escape(name),-12} {Markup.Escape(label)}[/]");
    }

    public static void WriteAgentTable(this IAnsiConsole console, IReadOnlyList<DiscoveredAgent> agents)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Agent").AddColumn("Mention").AddColumn("Status").AddColumn("Version").AddColumn("Location");

        foreach (var (_, result) in agents)
        {
            table.AddRow(
                Safe(result.DisplayName),
                Safe($"@{result.ProviderId}"),
                result.IsInstalled ? "[green]installed[/]" : "[yellow]not found[/]",
                Safe(result.Version ?? "-"),
                Safe(result.ExecutablePath ?? result.Detail ?? "-"));
        }

        console.Write(table);
    }
}
