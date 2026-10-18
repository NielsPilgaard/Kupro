using System.Diagnostics;
using System.Text.Json;

namespace Kupro.BackupAgent;

public enum JobKind
{
    Backup,
    Drill,
    Verify,
    Restore,
    DeleteOldVolume,
}

public sealed record JobOutcome(bool Ok, string Summary, object? Details = null);

/// <summary>A heavy run (backup, drill, verify, restore). Its log is shown live in the console.</summary>
public sealed class Job(JobKind kind, string title)
{
    private const int KeptLines = 5000;
    private readonly List<string> _lines = [];

    public Guid Id { get; } = Guid.NewGuid();
    public JobKind Kind { get; } = kind;
    public string Title { get; } = title;
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; private set; }
    public bool? Ok { get; private set; }
    public string? Summary { get; private set; }

    public void Log(string line)
    {
        lock (_lines)
        {
            _lines.Add($"{Fmt.Local(DateTimeOffset.UtcNow):HH:mm:ss}  {line}");
            if (_lines.Count > KeptLines)
            {
                _lines.RemoveAt(0);
            }
        }
    }

    /// <summary>Lines from index <paramref name="from"/>, for the console's live log.</summary>
    public (IReadOnlyList<string> Lines, int Next) LinesFrom(int from)
    {
        lock (_lines)
        {
            from = Math.Clamp(from, 0, _lines.Count);
            return (_lines.GetRange(from, _lines.Count - from), _lines.Count);
        }
    }

    public void Finish(JobOutcome outcome)
    {
        Summary = outcome.Summary;
        Ok = outcome.Ok;
        EndedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Runs one heavy job at a time, so a drill never competes with a backup or a restore for I/O and
/// memory. Every finished job lands in history.
/// </summary>
public sealed class JobRunner(OpsBucket ops, IHostApplicationLifetime lifetime, ILogger<JobRunner> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _recentGate = new();
    private readonly List<Job> _recent = [];

    public Job? Current { get; private set; }

    public IReadOnlyList<Job> Recent
    {
        get
        {
            lock (_recentGate)
            {
                return [.. _recent];
            }
        }
    }

    public Job? Find(Guid id)
    {
        lock (_recentGate)
        {
            return _recent.FirstOrDefault(j => j.Id == id);
        }
    }

    /// <summary>Starts the job in the background, or returns null when another job is running (console actions).</summary>
    public Job? TryStart(JobKind kind, string title, Func<Job, CancellationToken, Task<JobOutcome>> work)
    {
        if (!_gate.Wait(0))
        {
            return null;
        }

        var job = Begin(kind, title);
        _ = Task.Run(() => ExecuteAsync(job, work));
        return job;
    }

    /// <summary>Waits for the gate and runs the job (scheduled runs).</summary>
    public async Task<Job> RunAsync(JobKind kind, string title, Func<Job, CancellationToken, Task<JobOutcome>> work)
    {
        await _gate.WaitAsync(lifetime.ApplicationStopping);
        var job = Begin(kind, title);
        await ExecuteAsync(job, work);
        return job;
    }

    public void Dispose() => _gate.Dispose();

    private Job Begin(JobKind kind, string title)
    {
        var job = new Job(kind, title);
        Current = job;
        lock (_recentGate)
        {
            _recent.Insert(0, job);
            if (_recent.Count > 30)
            {
                _recent.RemoveAt(_recent.Count - 1);
            }
        }

        return job;
    }

    private async Task ExecuteAsync(Job job, Func<Job, CancellationToken, Task<JobOutcome>> work)
    {
        var stopwatch = Stopwatch.StartNew();
        JobOutcome outcome;
        try
        {
            job.Log($"Start: {job.Title}");
            outcome = await work(job, lifetime.ApplicationStopping);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {Title} failed", job.Title);
            job.Log($"ERROR: {ex.Message}");
            outcome = new JobOutcome(false, $"Failed: {ex.Message}");
        }
        finally
        {
            Current = null;
            _gate.Release();
        }

        job.Log(outcome.Ok ? $"Done: {outcome.Summary}" : $"Failed: {outcome.Summary}");
        job.Finish(outcome);
        try
        {
            var details = outcome.Details is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(outcome.Details, AgentJson.Options);
            await ops.AppendHistoryAsync(
                new HistoryEntry(job.StartedAt, job.Kind.ToString().ToLowerInvariant(), outcome.Ok, outcome.Summary, stopwatch.Elapsed.TotalSeconds, details),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write history for {Title}", job.Title);
        }
    }
}
