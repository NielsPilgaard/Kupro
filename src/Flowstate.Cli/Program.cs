namespace Flowstate.Cli;

/// <summary>Entry point. Namespaced (not top-level statements) so tests can reference both the API and the CLI.</summary>
public static class Program
{
    public static Task<int> Main(string[] args) => Cli.RunAsync(args);
}
