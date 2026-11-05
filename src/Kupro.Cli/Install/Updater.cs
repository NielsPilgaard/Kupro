using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Kupro.Cli.Install;

/// <summary>Replaces the binary from the public bucket, checksum verified (ADR-018).</summary>
public sealed partial class Updater(HttpClient http, string baseUrl, string rid)
{
    /// <summary>The public bucket. The release workflow and the install scripts hold the same URL.</summary>
    public const string DefaultBaseUrl = "https://kupro-downloads.s3.gra.io.cloud.ovh.net";

    public static Updater ForThisMachine(HttpClient http) => new(
        http, Environment.GetEnvironmentVariable("KUPRO_DOWNLOAD_URL") ?? DefaultBaseUrl, RuntimeInformation.RuntimeIdentifier);

    private string FileName => $"kupro-{rid}{(rid.StartsWith("win", StringComparison.Ordinal) ? ".exe" : "")}";

    /// <summary>The version <c>cli/latest</c> names.</summary>
    public async Task<string> LatestAsync(CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/cli/latest";
        using var response = await GetAsync(url, ct);
        var version = (await response.Content.ReadAsStringAsync(ct)).Trim();
        return VersionPattern().IsMatch(version) ? version : throw new CliException($"{url} doesn't hold a version.");
    }

    /// <summary>Downloads a version next to <paramref name="target"/>, verifies it, then swaps it in. A bad download changes nothing.</summary>
    public async Task ReplaceAsync(string version, string target, CancellationToken ct)
    {
        var folder = $"{baseUrl.TrimEnd('/')}/cli/{version}";
        var temp = target + ".new";
        try
        {
            string expected;
            using (var sums = await GetAsync($"{folder}/SHA256SUMS", ct))
            {
                expected = FindChecksum(await sums.Content.ReadAsStringAsync(ct))
                    ?? throw new CliException($"Release {version} has no build for {rid}.");
            }

            using (var binary = await GetAsync($"{folder}/{FileName}", ct))
            await using (var file = File.Create(temp))
            {
                await binary.Content.CopyToAsync(file, ct);
            }

            string actual;
            await using (var file = File.OpenRead(temp))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
            }

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new CliException($"The download of {FileName} doesn't match its checksum. Nothing was changed.");
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, (UnixFileMode)0b111_101_101);
            }

            Swap(temp, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CliException($"Can't replace {target}: {e.Message}");
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private string? FindChecksum(string sums) => sums
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .FirstOrDefault(parts => parts.Length == 2 && parts[1].TrimStart('*') == FileName)?[0];

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var status = (int)response.StatusCode;
            response.Dispose();
            throw new CliException($"Can't download {url}: the server answered {status}.");
        }
        catch (HttpRequestException e)
        {
            throw new CliException($"Can't download {url}: {e.Message}");
        }
    }

    private static void Swap(string temp, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Running copies, like MCP bridges, keep the old file open until they exit.
            File.Move(temp, target, overwrite: true);
            return;
        }

        // Windows can't overwrite a running exe, but it can rename one. Earlier copies go once nothing runs them.
        var folder = Path.GetDirectoryName(target)!;
        var pattern = Path.GetFileName(target) + ".*.old";
        foreach (var stale in Directory.EnumerateFiles(folder, pattern))
        {
            try
            {
                File.Delete(stale);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still running; the next update tries again.
            }
        }

        var old = $"{target}.{Guid.NewGuid():N}.old";
        File.Move(target, old);
        try
        {
            File.Move(temp, target);
        }
        catch
        {
            File.Move(old, target);
            throw;
        }
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionPattern();
}
