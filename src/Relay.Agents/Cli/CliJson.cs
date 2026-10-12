using System.Text.Json;

namespace Relay.Agents.Cli;

public static class CliJson
{
    public static string? GetStringOrNull(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Parses text that should be one JSON object. The caller disposes the document when this returns true.</summary>
    public static bool TryParseObject(ReadOnlyMemory<char> line, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }

        if (document.RootElement.ValueKind == JsonValueKind.Object)
            return true;

        document.Dispose();
        document = null!;
        return false;
    }
}
