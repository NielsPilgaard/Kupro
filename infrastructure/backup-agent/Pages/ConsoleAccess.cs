using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Kupro.BackupAgent.Pages;

/// <summary>
/// The console's only gate besides the SSH tunnel. The agent sits on its own network, but testing on
/// Docker Desktop (in Skoleoverblikket, where this agent comes from) showed a container on another
/// network reaching it by IP, masqueraded to the same gateway address as the
/// tunnel. So the agent keeps a random key in its own volume, and the browser needs it once. Reading
/// it takes <c>docker exec</c> on the host, which the API container can't do: whoever has a shell on
/// the host is still the identity, and nothing else gets in.
/// </summary>
public sealed class ConsoleAccess
{
    public const string CookieName = "backup-console-key";
    private static readonly string[] OpenPaths = ["/healthz", "/console.css", "/console.js", "/favicon.svg", "/access"];

    private readonly byte[] _key;

    public ConsoleAccess(IOptions<AgentOptions> options, ILogger<ConsoleAccess> logger)
    {
        var directory = options.Value.StateDirectory;
        Directory.CreateDirectory(directory);
        var keyFile = Path.Combine(directory, "console-key");
        if (!File.Exists(keyFile))
        {
            File.WriteAllText(keyFile, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
            File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var key = string.IsNullOrWhiteSpace(options.Value.ConsoleKey) ? File.ReadAllText(keyFile).Trim() : options.Value.ConsoleKey.Trim();
        _key = Encoding.ASCII.GetBytes(key);
        var linkFile = Path.Combine(directory, "console-link");
        File.WriteAllText(linkFile, $"http://localhost:9090/access?key={key}\n");
        File.SetUnixFileMode(linkFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        logger.LogInformation("Console link: run `docker exec <backup-agent container> cat /var/lib/backup-agent/console-link` on the host");
    }

    public bool Matches(string? candidate) =>
        candidate is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), _key);

    public static bool IsOpen(PathString path) => OpenPaths.Any(p => path.Equals(p, StringComparison.Ordinal));

    public static void Map(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var access = context.RequestServices.GetRequiredService<ConsoleAccess>();
            if (IsOpen(context.Request.Path) || access.Matches(context.Request.Cookies[CookieName]))
            {
                await next();
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(LockedPage);
        });

        app.MapGet("/access", (HttpContext context, string? key, ConsoleAccess access) =>
        {
            if (!access.Matches(key))
            {
                return Results.Content(LockedPage, "text/html; charset=utf-8", statusCode: StatusCodes.Status401Unauthorized);
            }

            context.Response.Cookies.Append(CookieName, key!, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = false, // Plain http through the SSH tunnel.
                MaxAge = TimeSpan.FromHours(12),
                Path = "/",
            });
            return Results.Redirect("/");
        });
    }

    private const string LockedPage = """
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Backup console</title><link rel="stylesheet" href="/console.css"></head>
        <body><main>
        <h1>Backup console</h1>
        <p>Get the access link from the server. In your SSH session:</p>
        <pre class="log">docker exec $(docker ps -qf label=com.docker.compose.service=backup-agent) cat /var/lib/backup-agent/console-link</pre>
        <p class="muted">Open it through the tunnel (<code>ssh -L 9090:127.0.0.1:9090 &lt;vps&gt;</code>). Access lasts 12 hours.</p>
        </main></body>
        </html>
        """;
}
