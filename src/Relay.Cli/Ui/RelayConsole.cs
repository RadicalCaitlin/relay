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
        => Markup.Escape(string.Concat(text.Where(c => c is '\n' or '\t' || !(char.IsControl(c) || IsBidiControl(c)))));

    private static bool IsBidiControl(char c)
        => c is (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩') or '‎' or '‏' or '؜';

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
