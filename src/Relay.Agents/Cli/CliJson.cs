using System.Text.Json;

namespace Relay.Agents.Cli;

internal static class CliJson
{
    public static string? GetStringOrNull(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
