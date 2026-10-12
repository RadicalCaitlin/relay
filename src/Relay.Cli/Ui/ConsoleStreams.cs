namespace Relay.Cli.Ui;

/// <summary>
/// Whether the console's own streams have been redirected. A seam, so a test can state each
/// condition on its own: the question Relay asks about a folder cannot be reached otherwise,
/// because a test harness redirects every stream. It is supplied in code, never by the
/// environment: a folder's trust must not turn on anything outside the user's own answer.
/// </summary>
internal interface IConsoleStreams
{
    bool IsInputRedirected { get; }

    bool IsOutputRedirected { get; }

    /// <summary>Throws away keys already pressed and not yet read, so they cannot answer a question asked after them.</summary>
    void DiscardPendingInput();
}

internal sealed class ConsoleStreams : IConsoleStreams
{
    public bool IsInputRedirected => Console.IsInputRedirected;

    public bool IsOutputRedirected => Console.IsOutputRedirected;

    public void DiscardPendingInput()
    {
        // With input from a file or a pipe there are no keys to look for, and looking throws.
        if (Console.IsInputRedirected)
            return;

        while (Console.KeyAvailable)
            Console.ReadKey(intercept: true);
    }
}
