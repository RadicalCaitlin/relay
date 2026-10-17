namespace Relay.Cli.Infrastructure;

internal static class RelayExecutable
{
    /// <summary>
    /// The program and arguments that run this same Relay with the given arguments, whether it was
    /// started as its own executable or as a library through <c>dotnet</c>.
    /// </summary>
    public static (string FileName, IReadOnlyList<string> Arguments) With(params string[] arguments)
    {
        var host = Environment.ProcessPath!;
        return Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? (host, [typeof(RelayExecutable).Assembly.Location, .. arguments])
            : (host, arguments);
    }
}
