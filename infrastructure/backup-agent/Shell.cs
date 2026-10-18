using System.Diagnostics;

namespace Kupro.BackupAgent;

public sealed record ShellResult(int ExitCode, IReadOnlyList<string> Output)
{
    public bool Ok => ExitCode == 0;

    public string Tail(int lines = 5) => string.Join('\n', Output.TakeLast(lines));
}

/// <summary>Runs pgBackRest, pg_ctl and friends. Arguments are passed as a list, never through a shell.</summary>
public static class Shell
{
    private const int KeptLines = 2000;

    public static async Task<ShellResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        Action<string>? onLine = null,
        CancellationToken cancellationToken = default)
    {
        using var process = Start(fileName, arguments);
        var output = new List<string>();

        void Collect(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                output.Add(line);
                if (output.Count > KeptLines)
                {
                    output.RemoveAt(0);
                }
            }

            onLine?.Invoke(line);
        }

        process.OutputDataReceived += (_, e) => Collect(e.Data);
        process.ErrorDataReceived += (_, e) => Collect(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        // WaitForExitAsync can return before the last redirected lines are delivered.
        process.WaitForExit();
        lock (output)
        {
            return new ShellResult(process.ExitCode, [.. output]);
        }
    }

    /// <summary>Starts a long-running process (pg_receivewal). The caller owns and disposes it.</summary>
    public static Process Start(string fileName, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info) ?? throw new InvalidOperationException($"Could not start {fileName}");
    }
}
