namespace Relay.Core.Agents;

public enum AgentActivityKind
{
    /// <summary>Something the agent said before its reply, such as what it is about to do.</summary>
    Note,

    /// <summary>A step the agent took: a file it read or changed, a command it ran.</summary>
    Action,

    /// <summary>A step that was refused or that failed.</summary>
    Problem,

    /// <summary>The permission mode the agent says it is running this turn in, such as <c>auto</c>.</summary>
    Mode,
}

/// <summary>
/// What an agent reports while it works on a turn, before its reply. It is agent output, so it is
/// untrusted, and it is only ever shown to the user: it is not part of the room and no other agent sees it.
/// </summary>
/// <param name="Tool">For a step, the name of the tool or skill it used, when the agent reports one.</param>
public sealed record AgentActivity(AgentActivityKind Kind, string Text, string? Tool = null);
