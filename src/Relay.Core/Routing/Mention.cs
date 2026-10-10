using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Relay.Core.Routing;

public static partial class Mention
{
    /// <summary>Parses a leading <c>@target</c>. Mentions elsewhere in the text are not routing instructions.</summary>
    public static bool TryParse(string text, [NotNullWhen(true)] out string? target, [NotNullWhen(true)] out string? body)
    {
        var match = LeadingMention().Match(text);
        target = match.Success ? match.Groups["target"].Value : null;
        body = match.Success ? match.Groups["body"].Value.Trim() : null;
        return match.Success;
    }

    /// <summary>
    /// Parses an agent's handover: <c>@target</c> alone on the first line, the message on the lines
    /// after. Stricter than <see cref="TryParse"/> on purpose, because a reply written for the user
    /// can easily begin by naming another agent ("@claude and I agree").
    /// </summary>
    /// <param name="body">Empty when nothing follows the first line.</param>
    public static bool TryParseHandover(string reply, [NotNullWhen(true)] out string? target, [NotNullWhen(true)] out string? body)
    {
        var match = HandoverLine().Match(reply);
        target = match.Success ? match.Groups["target"].Value : null;
        body = match.Success ? match.Groups["body"].Value.Trim() : null;
        return match.Success;
    }

    [GeneratedRegex(@"^\s*@(?<target>[A-Za-z0-9_-]+)(?:[,:]\s*|\s+|$)(?<body>.*)$", RegexOptions.Singleline)]
    private static partial Regex LeadingMention();

    [GeneratedRegex(@"^\s*@(?<target>[A-Za-z0-9_-]+)[ \t]*(?:\r?\n(?<body>.*))?$", RegexOptions.Singleline)]
    private static partial Regex HandoverLine();
}
