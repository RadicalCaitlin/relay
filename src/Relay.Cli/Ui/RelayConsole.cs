using Relay.Core.Agents;
using Spectre.Console;

namespace Relay.Cli.Ui;

internal static class RelayConsole
{
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
                Markup.Escape(result.DisplayName),
                Markup.Escape($"@{result.ProviderId}"),
                result.IsInstalled ? "[green]installed[/]" : "[yellow]not found[/]",
                Markup.Escape(result.Version ?? "-"),
                Markup.Escape(result.ExecutablePath ?? result.Detail ?? "-"));
        }

        console.Write(table);
    }
}
