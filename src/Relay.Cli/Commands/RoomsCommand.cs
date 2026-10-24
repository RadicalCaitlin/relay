using System.ComponentModel;
using System.Globalization;
using Relay.Cli.Ui;
using Relay.Core.Rooms;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Relay.Cli.Commands;

internal sealed class RoomsCommand(IAnsiConsole console, RoomStore roomStore) : Command<RoomsCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandOption("--delete <NAME>")]
        [Description("Delete the room saved under this name for the current folder, with everything said in it.")]
        public string? Delete { get; init; }

        [CommandOption("--workspace <PATH>")]
        [Description("With --delete: the folder the room belongs to, instead of the current folder.")]
        public string? Workspace { get; init; }

        public override ValidationResult Validate()
        {
            if (Workspace is not null && Delete is null)
                return ValidationResult.Error("--workspace is only used with --delete.");
            if (Delete is not null && !RoomStore.IsValidName(Delete))
                return ValidationResult.Error($"--delete must name a room: letters, digits, - and _, up to {RoomStore.MaxNameLength} characters.");
            if (Workspace is not null)
            {
                if (StartCommand.Settings.ResolveWorkspace(Workspace) is not { } folder)
                    return ValidationResult.Error($"--workspace must name a folder that exists. Not found: {RelayConsole.PlainLine(Workspace)}");
                WorkspacePath = folder;
            }

            return ValidationResult.Success();
        }

        /// <summary>The folder <see cref="Workspace"/> names, as resolved when the options were checked.</summary>
        internal string? WorkspacePath { get; private set; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            if (settings.Delete is { } name)
            {
                var folder = settings.WorkspacePath ?? Environment.CurrentDirectory;
                console.MarkupLine(roomStore.Delete(folder, name)
                    ? $"Deleted room {Markup.Escape(name)} for {RelayConsole.SafeLine(folder)}."
                    : $"[dim]No room named {Markup.Escape(name)} is saved for {RelayConsole.SafeLine(folder)}. relay rooms lists the saved rooms.[/]");
                return 0;
            }

            var rooms = roomStore.List();
            if (rooms.Count == 0)
            {
                console.MarkupLine("[dim]No saved rooms. relay start --room <name> saves one.[/]");
                return 0;
            }

            var table = new Table().Border(TableBorder.Simple)
                .AddColumn("Room").AddColumn("Folder").AddColumn("Messages").AddColumn("Last saved");
            foreach (var room in rooms)
            {
                table.AddRow(
                    Markup.Escape(room.Name),
                    room.Workspace is { } workspace ? RelayConsole.SafeLine(workspace) : "[yellow]could not be read[/]",
                    room.Messages?.ToString(CultureInfo.InvariantCulture) ?? "-",
                    room.LastSaved.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            }

            console.Write(table);
            console.MarkupLine($"[dim]Saved in {RelayConsole.SafeLine(roomStore.Folder!)}, where only you can read them. relay rooms --delete <name> deletes one for the current folder.[/]");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            console.MarkupLine($"[red]Could not use the saved rooms: {RelayConsole.SafeLine(ex.Message)}[/]");
            return 1;
        }
    }
}
