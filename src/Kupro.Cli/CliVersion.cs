using System.Reflection;

namespace Kupro.Cli;

/// <summary>The version stamped into the binary by the release workflow (ADR-018).</summary>
public static class CliVersion
{
    /// <summary>A local build, from the csproj. Never warns; it tracks the source, not a release.</summary>
    public const string Unstamped = "0.0.0";

    public static string Current { get; } =
        typeof(CliVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? Unstamped;

    /// <summary>The message for a CLI older than the server's minimum; null when it is fine or either can't be read.</summary>
    public static string? Warning(string current, string? minimum) =>
        current != Unstamped && Parse(current) is { } mine && Parse(minimum) is { } required && mine < required
            ? $"warning: this CLI ({current}) is older than this server supports ({minimum}). Run `kupro update`."
            : null;

    private static Version? Parse(string? version) =>
        Version.TryParse(version?.Split('-', '+')[0], out var parsed) ? parsed : null;
}
