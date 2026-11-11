using System.Text.Json;
using System.Threading.Channels;
using Kupro.Api.Data;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Attachments;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Realtime;
using Kupro.Api.Tenancy;
using Kupro.Import.Snapshot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kupro.Api.Features.Imports;

public sealed class ImportOptions
{
    public const string Section = "Imports";

    /// <summary>Imports running at once across workspaces, so a big one can't starve exports or the meeting agent (ADR-019).</summary>
    public int MaxConcurrent { get; set; } = 2;
}

/// <summary>Where an import's stored snapshot lives. Plans and commits read this copy, so re-planning doesn't read the source. Singleton.</summary>
public sealed class ImportSnapshots(ObjectStorage storage)
{
    public bool Enabled => storage.Enabled;

    public Task StoreAsync(Guid workspaceId, Guid importId, string path, CancellationToken ct) =>
        storage.UploadFileAsync(ObjectKey(workspaceId, importId), path, "application/zip", ct);

    public async Task<bool> ExistsAsync(Guid workspaceId, Guid importId, CancellationToken ct) =>
        await storage.SizeAsync(ObjectKey(workspaceId, importId), ct) is not null;

    /// <summary>A local, seekable copy (a ZIP is read from its end), deleted when the stream closes. Null if none is stored.</summary>
    public async Task<FileStream?> OpenAsync(Guid workspaceId, Guid importId, CancellationToken ct)
    {
        await using var remote = await storage.OpenReadAsync(ObjectKey(workspaceId, importId), ct);
        if (remote is null)
        {
            return null;
        }

        var local = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await remote.CopyToAsync(local, ct);
            local.Position = 0;
            return local;
        }
        catch
        {
            await local.DisposeAsync();
            throw;
        }
    }

    private static string ObjectKey(Guid workspaceId, Guid importId) => $"{workspaceId}/imports/{importId}/snapshot.zip";
}

/// <summary>Wakes <see cref="ImportJob"/> when a run is queued or finishes. Singleton.</summary>
public sealed class ImportSignal
{
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Signal() => _signal.Writer.TryWrite(true);

    /// <summary>Waits for a signal or the timeout, whichever comes first.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _signal.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }
}

/// <summary>Queues an import's runs and stores its snapshot. The REST, CLI and web surfaces come on top of this.</summary>
public sealed partial class ImportService(
    IDbContextFactory<KuproDbContext> dbFactory,
    WorkspaceContext workspace,
    ImportSnapshots snapshots,
    ImportSignal signal,
    IssueService issues,
    LabelService labels,
    RealtimeEvents realtime,
    TimeProvider clock)
{
    /// <summary>Replaces the import's stored snapshot, after checking it can be read.</summary>
    public async Task<IssueResult<SnapshotHeader>> StoreSnapshotAsync(Guid importId, string path, CancellationToken ct)
    {
        if (!snapshots.Enabled)
        {
            return Unavailable;
        }

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (!await db.WorkspaceImports.AnyAsync(i => i.Id == importId, ct))
            {
                return new IssueError("import_not_found", $"No import {importId}.");
            }
        }

        SnapshotHeader header;
        try
        {
            await using var file = File.OpenRead(path);
            await using var reader = await SnapshotReader.OpenAsync(file, ct);
            header = reader.Header;
        }
        catch (SnapshotFormatException ex)
        {
            return new IssueError("invalid_snapshot", ex.Message);
        }

        await snapshots.StoreAsync(workspace.RequireWorkspaceId(), importId, path, ct);
        return header;
    }

    /// <summary>Queues a dry run or a commit. The job runs one at a time per workspace, so a second import simply waits.</summary>
    public async Task<IssueResult<ImportRun>> QueueAsync(Guid importId, ImportRunKind kind, CancellationToken ct)
    {
        if (!snapshots.Enabled)
        {
            return Unavailable;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var import = await db.WorkspaceImports.AsNoTracking().SingleOrDefaultAsync(i => i.Id == importId, ct);
        if (import is null)
        {
            return new IssueError("import_not_found", $"No import {importId}.");
        }

        if (import.State != ImportState.Active)
        {
            return new IssueError("import_closed", "This import is finished or undone.");
        }

        if (import.Mapping is null)
        {
            return new IssueError("no_mapping", "Save a mapping before planning or running the import.");
        }

        if (!await snapshots.ExistsAsync(import.WorkspaceId, importId, ct))
        {
            return new IssueError("no_snapshot", "The import has no snapshot yet.");
        }

        var run = new ImportRun
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = import.WorkspaceId,
            ImportId = importId,
            Kind = kind,
            State = ImportRunState.Queued,
            CreatedAt = clock.GetUtcNow(),
        };
        db.ImportRuns.Add(run);
        await db.SaveChangesAsync(ct);
        signal.Signal();
        return run;
    }

    private static IssueError Unavailable => new("imports_unavailable", "File storage is not configured on this server.");
}

/// <summary>
/// The import's own queue (ADR-019 Who and where): one run per workspace, <see cref="ImportOptions.MaxConcurrent"/>
/// across workspaces, oldest first. A run left Running by a restart is picked up again and resumes from what it committed.
/// Assumes one API instance, as <c>ExportJob</c> does: a second one would pick up the same runs.
/// </summary>
public sealed partial class ImportJob(
    IServiceScopeFactory scopes, ImportSnapshots snapshots, ImportSignal signal, IOptions<ImportOptions> options, TimeProvider clock, ILogger<ImportJob> logger) : BackgroundService
{
    private readonly Dictionary<Guid, (Guid WorkspaceId, Task Run)> _running = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!snapshots.Enabled)
        {
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await StartWaitingAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailed(logger, ex);
                }

                await signal.WaitAsync(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
        finally
        {
            // Each run stops at its next batch and stays Running, so the next start resumes it.
            await Task.WhenAll(_running.Values.Select(r => r.Run));
        }
    }

    private async Task StartWaitingAsync(CancellationToken ct)
    {
        foreach (var finished in _running.Where(r => r.Value.Run.IsCompleted).Select(r => r.Key).ToList())
        {
            _running.Remove(finished);
        }

        var room = Math.Max(1, options.Value.MaxConcurrent) - _running.Count;
        if (room <= 0)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(ct);
        // System job across all tenants: finds every workspace's waiting runs; each then runs inside its own workspace.
        var waiting = await db.ImportRuns.IgnoreQueryFilters([KuproDbContext.TenantFilter]).AsNoTracking()
            .Where(r => r.State == ImportRunState.Queued || r.State == ImportRunState.Running)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Select(r => new { r.Id, r.WorkspaceId })
            .ToListAsync(ct);

        var busy = _running.Values.Select(r => r.WorkspaceId).ToHashSet();
        foreach (var run in waiting)
        {
            // Marks the workspace busy whether or not this run starts, so its later runs can't overtake it.
            if (room > 0 && busy.Add(run.WorkspaceId))
            {
                _running[run.Id] = (run.WorkspaceId, RunAndSignalAsync(run.Id, run.WorkspaceId, ct));
                room--;
            }
        }
    }

    private async Task RunAndSignalAsync(Guid runId, Guid workspaceId, CancellationToken ct)
    {
        // Off the loop: the first await inside a run can be a long way in.
        await Task.Yield();
        try
        {
            await RunAsync(runId, workspaceId, ct);
        }
        catch (OperationCanceledException)
        {
            // Shutting down, or a read that timed out. The run stays Running and is picked up again.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRunFailed(logger, runId, ex);
        }
        finally
        {
            signal.Signal();
        }
    }

    private async Task RunAsync(Guid runId, Guid workspaceId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WorkspaceContext>();
        context.SetForSystem(workspaceId);
        context.Surface = Surface.Import;
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(ct);
        var run = await db.ImportRuns.SingleAsync(r => r.Id == runId, ct);
        var import = await db.WorkspaceImports.SingleAsync(i => i.Id == run.ImportId, ct);

        run.State = ImportRunState.Running;
        run.StartedAt ??= clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        try
        {
            if (import.State != ImportState.Active)
            {
                throw new ImportFailedException("This import is finished or undone.");
            }

            var mapping = import.Mapping is { } saved ? ImportMapping.FromJson(saved) : throw new ImportFailedException("Save a mapping before running the import.");
            await using var file = await snapshots.OpenAsync(workspaceId, import.Id, ct) ?? throw new ImportFailedException("The import's snapshot is gone. Upload it again.");
            await using var snapshot = await SnapshotReader.OpenAsync(file, ct);
            if (run.Kind == ImportRunKind.DryRun)
            {
                var plan = await scope.ServiceProvider.GetRequiredService<ImportCommit>().PlanAsync(import, snapshot, mapping, ct);
                run.Report = JsonSerializer.Serialize(plan, JsonSerializerOptions.Web);
            }
            else
            {
                await CommitAsync(scope, db, run, import, snapshot, mapping, ct);
            }

            run.State = ImportRunState.Succeeded;
            run.CompletedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is not (ImportFailedException or SnapshotFormatException))
            {
                LogRunFailed(logger, runId, ex);
            }

            run.State = ImportRunState.Failed;
            run.CompletedAt = clock.GetUtcNow();
            run.Error = ex is ImportFailedException or SnapshotFormatException ? Cut(ex.Message) : "The import failed unexpectedly. It can be run again; it continues from what it finished.";
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task CommitAsync(AsyncServiceScope scope, KuproDbContext db, ImportRun run, WorkspaceImport import, SnapshotReader snapshot, ImportMapping mapping, CancellationToken ct)
    {
        var commit = scope.ServiceProvider.GetRequiredService<ImportCommit>();
        var counts = await commit.RunAsync(import, run, snapshot, mapping, ct);

        // The batches saved their progress on their own contexts; this one is about to write the final numbers.
        await db.Entry(run).ReloadAsync(ct);
        run.Counts = JsonSerializer.Serialize(counts, JsonSerializerOptions.Web);
        run.Report = JsonSerializer.Serialize(commit.Report, JsonSerializerOptions.Web);
        var now = clock.GetUtcNow();
        import.Cursor = snapshot.Header.Cursor;
        import.LastRunAt = now;
        if (counts.Issues + counts.Updated + counts.Comments + counts.Attachments + counts.Changes + counts.SourceChanges > 0)
        {
            import.LastChangeAt = now;
        }
    }

    private static string Cut(string message) => message.Length > 2000 ? message[..2000] : message;

    [LoggerMessage(Level = LogLevel.Error, Message = "Import queue failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Import run {RunId} failed")]
    private static partial void LogRunFailed(ILogger logger, Guid runId, Exception exception);
}
