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

    [GeneratedRegex(@"^\s*@(?<target>[A-Za-z0-9_-]+)[,:]?(?:\s+(?<body>.*))?$", RegexOptions.Singleline)]
    private static partial Regex LeadingMention();
}
