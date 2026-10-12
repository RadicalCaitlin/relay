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
}

internal sealed class ConsoleStreams : IConsoleStreams
{
    public bool IsInputRedirected => Console.IsInputRedirected;

    public bool IsOutputRedirected => Console.IsOutputRedirected;
}
