namespace Relay.Cli.Ui;

/// <summary>What the user sent in: the text, and how many lines it arrived as.</summary>
/// <param name="Text">Trimmed when it is a single line, as a typed line has always been. A block of
/// several lines is kept exactly as it arrived, so its indentation and blank lines survive.</param>
internal readonly record struct TypedInput(string Text, int Lines);

/// <summary>
/// Reads one message from the keyboard. Lines that arrive together are one message: a paste is not
/// several messages, and a line of it is never read as a /command. Relay reads a line at a time, so
/// a paste can only be recognised by the rest of it already waiting; a terminal that reports a
/// bracketed paste needs the key-by-key editor of #40.
/// </summary>
internal sealed class PastedInput(IConsoleStreams streams, TimeProvider time)
{
    /// <summary>
    /// How long after a line Relay keeps waiting for the rest of a paste. Codex CLI, which has the
    /// same problem on Windows, raised its own threshold from 8ms to 30ms after pastes in the
    /// VS Code terminal were split.
    /// </summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(30);

    private static readonly TimeSpan BetweenPolls = TimeSpan.FromMilliseconds(1);

    /// <summary>The next message, or null at the end of input. Blocks, and cannot be abandoned once begun.</summary>
    public TypedInput? Read()
    {
        if (streams.ReadLine() is not { } first)
            return null;

        // Nothing arrives "together" through a file or a pipe: every line there is its own message,
        // as it always has been, so piped input and the test harness behave as before.
        if (streams.IsInputRedirected)
            return new(first.Trim(), 1);

        var lines = new List<string>();
        var line = first;
        while (true)
        {
            // A line ending in backslash is a new line the user typed on purpose, so Relay waits for
            // what follows however long it takes. The backslash itself is not part of the message.
            var continued = line.EndsWith('\\');
            if (continued)
                line = line[..^1];

            lines.Add(line);
            if (!continued && !MoreIsArriving())
                break;

            // The end of input ends the block, including one a backslash promised more of.
            if (streams.ReadLine() is not { } next)
                break;

            line = next;
        }

        return lines.Count == 1 ? new(lines[0].Trim(), 1) : new(string.Join('\n', lines), lines.Count);
    }

    private bool MoreIsArriving()
    {
        var started = time.GetTimestamp();
        while (true)
        {
            // Checked before the input is, so a line that only becomes available once the window has
            // passed starts a message of its own.
            if (time.GetElapsedTime(started) > Grace)
                return false;
            if (streams.InputWaiting)
                return true;

            Thread.Sleep(BetweenPolls);
        }
    }
}
