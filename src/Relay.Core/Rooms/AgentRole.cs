using System.Diagnostics.CodeAnalysis;

namespace Relay.Core.Rooms;

/// <summary>
/// What the user has asked an agent to be in a room. It is something the agent is told, never a
/// limit on what it can do. See docs/adr/0003-agent-roles.md.
/// </summary>
public sealed record AgentRole
{
    // Long enough for a sentence or two. A role is sent on every turn, so it must not become a second prompt.
    public const int MaxDescriptionLength = 500;

    private AgentRole(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public string Name { get; }

    public string Description { get; }

    /// <summary>The description, ending like a sentence so that what follows does not run into it.</summary>
    public string Sentence => Description[^1] is '.' or '!' or '?' ? Description : Description + ".";

    /// <summary>No description names a provider: any agent can hold any role.</summary>
    public static IReadOnlyList<AgentRole> BuiltIn { get; } =
    [
        new("implementer", "You write and change the code. Make the smallest change that does what was asked, run the build and tests, and say what you did and what you could not verify."),
        new("reviewer", "You review what others propose or change. Look for defects, risks and missing tests, and say what must change and what is optional. Do not make the changes yourself unless you are asked to."),
        new("architect", "You decide structure before code is written: components, boundaries and trade-offs. Give a recommendation with your reasons, and leave the implementation to others unless you are asked to do it."),
        new("coordinator", "You break the task into parts, hand each part to the agent best placed to do it, and report the combined result to the user. Do little of the work yourself."),
    ];

    public static AgentRole? FindBuiltIn(string name)
        => BuiltIn.FirstOrDefault(role => string.Equals(role.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A role of the user's own.</summary>
    /// <param name="error">Why it cannot be used, in words that can be shown to the user.</param>
    public static bool TryCreate(string name, string description, [NotNullWhen(true)] out AgentRole? role, [NotNullWhen(false)] out string? error)
    {
        role = null;
        name = name.Trim().ToLowerInvariant();
        description = description.Trim();

        if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            error = "A role name is letters, digits and hyphens only.";
        else if (!name.Any(char.IsAsciiLetterOrDigit))
            error = "A role name needs at least one letter or digit.";
        else if (name == "none")
            error = "\"none\" removes a role, so it cannot be the name of one.";
        else if (description.Length == 0)
            error = "A role needs a description after the colon.";
        else if (description.Length > MaxDescriptionLength)
            error = $"A role description is at most {MaxDescriptionLength} characters; this one is {description.Length}.";
        else
        {
            role = new(name, description);
            error = null;
        }

        return role is not null;
    }
}
