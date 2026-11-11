using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Kupro.Api.Data;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Attachments;
using Kupro.Api.Features.Email;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Realtime;
using Kupro.Api.Tenancy;
using Kupro.Import;
using Kupro.Import.Csv;
using Kupro.Import.Kupro;
using Kupro.Import.Snapshot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kupro.Api.Features.Imports;

public sealed class ImportOptions
{
    public const string Section = "Imports";

    /// <summary>Imports running at once across workspaces, so a big one can't starve exports or the meeting agent (ADR-019).</summary>
    public int MaxConcurrent { get; set; } = 2;

    /// <summary>Kupro's Trello API key. Without it, Trello is imported from a snapshot only.</summary>
    public string? TrelloKey { get; set; }
}

/// <summary>Where an import's stored snapshot lives. Plans and commits read this copy, so re-planning doesn't read the source. Singleton.</summary>
public sealed class ImportSnapshots(ObjectStorage storage)
{
    public bool Enabled => storage.Enabled;

    public Task StoreAsync(Guid workspaceId, Guid importId, string path, CancellationToken ct) =>
        storage.UploadFileAsync(ObjectKey(workspaceId, importId), path, "application/zip", ct);

    public async Task<bool> ExistsAsync(Guid workspaceId, Guid importId, CancellationToken ct) =>
        await storage.SizeAsync(ObjectKey(workspaceId, importId), ct) is not null;

    public Task DeleteAsync(Guid workspaceId, Guid importId, CancellationToken ct) => storage.DeleteAsync(ObjectKey(workspaceId, importId), ct);

    /// <summary>A file the source is read from when a run starts, like a CSV; the snapshot is made from it then.</summary>
    public Task StoreSourceAsync(Guid workspaceId, Guid importId, string path, string contentType, CancellationToken ct) =>
        storage.UploadFileAsync(SourceKey(workspaceId, importId), path, contentType, ct);

    public Task<Stream?> OpenSourceAsync(Guid workspaceId, Guid importId, CancellationToken ct) => storage.OpenReadAsync(SourceKey(workspaceId, importId), ct);

    /// <summary>The uploaded file as a local, seekable copy, deleted when the stream closes. Null if none is stored.</summary>
    public async Task<FileStream?> OpenLocalSourceAsync(Guid workspaceId, Guid importId, CancellationToken ct)
    {
        await using var remote = await storage.OpenReadAsync(SourceKey(workspaceId, importId), ct);
        if (remote is null)
        {
            return null;
        }

        var local = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        await remote.CopyToAsync(local, ct);
        local.Position = 0;
        return local;
    }

    public async Task<bool> SourceExistsAsync(Guid workspaceId, Guid importId, CancellationToken ct) =>
        await storage.SizeAsync(SourceKey(workspaceId, importId), ct) is not null;

    private static string SourceKey(Guid workspaceId, Guid importId) => $"{workspaceId}/imports/{importId}/source";

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
    ImportTokens tokens,
    ImportSources sources,
    IssueService issues,
    LabelService labels,
    AttachmentService attachments,
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

        // An import Kupro reads itself, or from an uploaded file, makes its snapshot when the run starts.
        if (import.EncryptedToken is null && !await snapshots.ExistsAsync(import.WorkspaceId, importId, ct)
            && !await snapshots.SourceExistsAsync(import.WorkspaceId, importId, ct))
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
    IServiceScopeFactory scopes, ImportSnapshots snapshots, ImportSources sources, ImportSignal signal, IOptions<ImportOptions> options, ImportMetrics metrics,
    TimeProvider clock, ILogger<ImportJob> logger) : BackgroundService
{
    private static readonly string[] TenantFilter = [KuproDbContext.TenantFilter];

    /// <summary>How often auto-sync runs an import (ADR-019 Mode).</summary>
    public static readonly TimeSpan SyncEvery = TimeSpan.FromHours(1);

    /// <summary>Auto-sync turns itself off after this long with no change on either side.</summary>
    public static readonly TimeSpan SyncQuietFor = TimeSpan.FromDays(30);

    /// <summary>A token unused this long is deleted: a migration nobody runs shouldn't keep a key to the source (ADR-019).</summary>
    public static readonly TimeSpan TokenKeptFor = TimeSpan.FromDays(90);

    private readonly Dictionary<Guid, (Guid WorkspaceId, Task Run)> _running = [];
    private DateTimeOffset _lastPurge;

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
                    if (clock.GetUtcNow() - _lastPurge > TimeSpan.FromHours(1))
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(stoppingToken);
                        await PurgeTokensAsync(db, clock.GetUtcNow(), stoppingToken);
                        if (await AutoSyncAsync(db, scope.ServiceProvider.GetRequiredService<IEmailSender>(), clock.GetUtcNow(), stoppingToken) > 0)
                        {
                            await StartWaitingAsync(stoppingToken);
                        }

                        _lastPurge = clock.GetUtcNow();
                    }
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

    /// <summary>Deletes source tokens of imports with no run for <see cref="TokenKeptFor"/>. Auto-sync stops with them.</summary>
    public static Task<int> PurgeTokensAsync(KuproDbContext db, DateTimeOffset now, CancellationToken ct) =>
        // System job across all tenants: the purge has no request workspace.
        db.WorkspaceImports.IgnoreQueryFilters([KuproDbContext.TenantFilter])
            .Where(i => i.EncryptedToken != null && (i.LastRunAt ?? i.CreatedAt) < now - TokenKeptFor)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.EncryptedToken, (string?)null).SetProperty(i => i.AutoSync, false), ct);

    /// <summary>
    /// Queues a run for every import due to sync, and turns auto-sync off for one that has been quiet on both sides for
    /// <see cref="SyncQuietFor"/>, telling its owner. Returns how many runs it queued.
    /// </summary>
    public static async Task<int> AutoSyncAsync(KuproDbContext db, IEmailSender email, DateTimeOffset now, CancellationToken ct)
    {
        // System job across all tenants: each import's runs and issues are matched by its own ids below.
        var due = await db.WorkspaceImports.IgnoreQueryFilters([KuproDbContext.TenantFilter])
            .Where(i => i.AutoSync && i.State == ImportState.Active && i.EncryptedToken != null && i.TokenRejectedAt == null && (i.LastRunAt ?? i.CreatedAt) <= now - SyncEvery)
            .Where(i => !db.ImportRuns.IgnoreQueryFilters(TenantFilter)
                .Any(r => r.ImportId == i.Id && (r.State == ImportRunState.Queued || r.State == ImportRunState.Running)))
            .ToListAsync(ct);

        var queued = 0;
        foreach (var import in due)
        {
            var issues = db.ImportedItems.IgnoreQueryFilters([KuproDbContext.TenantFilter])
                .Where(i => i.ImportId == import.Id && i.SourceKind == ImportedKinds.Item).Select(i => i.EntityId);
            var quiet = (import.LastChangeAt ?? import.CreatedAt) < now - SyncQuietFor
                && !await db.Activity.IgnoreQueryFilters([KuproDbContext.TenantFilter])
                    .AnyAsync(a => issues.Contains(a.IssueId) && a.Via != Surface.Import && a.CreatedAt > now - SyncQuietFor, ct);
            if (quiet)
            {
                import.AutoSync = false;
                await db.SaveChangesAsync(ct);
                await TellSyncOffAsync(db, email, import, ct);
                continue;
            }

            db.ImportRuns.Add(new ImportRun
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = import.WorkspaceId,
                ImportId = import.Id,
                Kind = ImportRunKind.Commit,
                State = ImportRunState.Queued,
                CreatedAt = now,
            });
            await db.SaveChangesAsync(ct);
            queued++;
        }

        return queued;
    }

    private static async Task TellSyncOffAsync(KuproDbContext db, IEmailSender email, WorkspaceImport import, CancellationToken ct)
    {
        var to = await db.Members.IgnoreQueryFilters([KuproDbContext.TenantFilter]).Where(m => m.Id == import.CreatedById)
            .Select(m => db.Users.Where(u => u.Id == m.UserId).Select(u => new { u.Email, u.Locale }).FirstOrDefault())
            .SingleOrDefaultAsync(ct);
        if (to is null)
        {
            return;
        }

        var workspace = await db.Workspaces.Where(w => w.Id == import.WorkspaceId).Select(w => w.Name).SingleAsync(ct);
        await email.SendAsync(ImportMail.SyncOff(to.Email, to.Locale, workspace, ImportDescription.SourceName(import.SourceKind)), ct);
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

        // A run resumed after a restart keeps the snapshot it started with: its saved progress counts through that one.
        var starting = run.StartedAt is null;
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
            if (starting && import.EncryptedToken is not null && NeedsRead(import, run))
            {
                await ReadSourceAsync(scope, db, import, mapping, ct);
            }
            else if (starting && import.SourceKind is SourceKinds.Csv or SourceKinds.Kupro)
            {
                // The uploaded file is converted with the mapping as saved now.
                await ReadFileAsync(db, import, mapping, ct);
            }

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

            var first = run.Kind == ImportRunKind.Commit
                && !await db.ImportRuns.AnyAsync(r => r.ImportId == import.Id && r.Kind == ImportRunKind.Commit && r.State == ImportRunState.Succeeded, ct);
            run.State = ImportRunState.Succeeded;
            run.CompletedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            metrics.RunEnded(run.Kind, run.State);
            if (first)
            {
                await NotifyAsync(scope, db, import, run, ct);
            }
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
            metrics.RunEnded(run.Kind, run.State);

            // A failed plan is shown to the owner who just asked for it; a failed commit may have run unattended.
            if (run.Kind == ImportRunKind.Commit)
            {
                await NotifyAsync(scope, db, import, run, ct);
            }
        }
    }

    /// <summary>Tells the owner who created the import, in their language (ADR-019 Email). The run stands whether or not it arrives.</summary>
    private async Task NotifyAsync(AsyncServiceScope scope, KuproDbContext db, WorkspaceImport import, ImportRun run, CancellationToken ct)
    {
        try
        {
            var to = await db.Members.Where(m => m.Id == import.CreatedById)
                .Select(m => db.Users.Where(u => u.Id == m.UserId).Select(u => new { u.Email, u.Locale }).FirstOrDefault())
                .SingleOrDefaultAsync(ct);
            if (to is null)
            {
                return;
            }

            var workspace = await db.Workspaces.Where(w => w.Id == import.WorkspaceId).Select(w => w.Name).SingleAsync(ct);
            var counts = run.Counts is { } json ? JsonSerializer.Deserialize<ImportRunCounts>(json, JsonSerializerOptions.Web) : null;
            var message = ImportMail.For(to.Email, to.Locale, workspace, ImportDescription.SourceName(import.SourceKind), run.State, counts?.Issues ?? 0, run.Error);
            await scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogNotifyFailed(logger, run.Id, ex);
        }
    }

    /// <summary>
    /// A commit reads what changed since the last one, unless a plan already read the source since then: the commit then
    /// writes what the owner just reviewed. A plan reads only when there is nothing yet to plan over.
    /// </summary>
    private static bool NeedsRead(WorkspaceImport import, ImportRun run) =>
        import.SourceReadAt is null || (run.Kind == ImportRunKind.Commit && import.SourceReadAt <= import.LastRunAt);

    /// <summary>Runs the import's connector into a fresh snapshot, from the saved cursor less the overlap, files included.</summary>
    private async Task ReadSourceAsync(AsyncServiceScope scope, KuproDbContext db, WorkspaceImport import, ImportMapping mapping, CancellationToken ct)
    {
        string token;
        try
        {
            token = scope.ServiceProvider.GetRequiredService<ImportTokens>().Unprotect(import.Id, import.EncryptedToken!);
        }
        catch (CryptographicException ex)
        {
            throw new ImportFailedException("Kupro can no longer read the stored token. Paste it again on the import.", ex);
        }

        var now = clock.GetUtcNow();
        var path = Path.GetTempFileName();
        try
        {
            await using (var file = File.Create(path))
            {
                var connector = sources.Create(import, token);
                await connector.WriteSnapshotAsync(file, ImportSources.Scope(mapping, now), ImportSources.WithOverlap(import.Cursor), includeFiles: true, clock, ct);
            }

            await snapshots.StoreAsync(import.WorkspaceId, import.Id, path, ct);
        }
        catch (SourceAccessException ex) when (ex.TokenRejected)
        {
            // Auto-sync waits for a new token; the run's failure email tells the owner.
            import.TokenRejectedAt = now;
            throw new ImportFailedException($"{ex.Message} Paste a new token on the import: the next run continues where this one stopped.", ex);
        }
        catch (SourceAccessException ex)
        {
            throw new ImportFailedException(ex.Message, ex);
        }
        finally
        {
            File.Delete(path);
        }

        import.SourceReadAt = now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Turns the uploaded file (a CSV, a Kupro export) into the snapshot the run reads, files included.</summary>
    private async Task ReadFileAsync(KuproDbContext db, WorkspaceImport import, ImportMapping mapping, CancellationToken ct)
    {
        // A ZIP is read from its end, so the upload comes down to a local file first.
        await using var source = await snapshots.OpenLocalSourceAsync(import.WorkspaceId, import.Id, ct) ?? throw new ImportFailedException("The import's file is gone. Upload it again.");
        var path = Path.GetTempFileName();
        try
        {
            await using (var file = File.Create(path))
            {
                var scope = ImportSources.Scope(mapping, clock.GetUtcNow());
                if (import.SourceKind == SourceKinds.Csv)
                {
                    var table = await CsvTable.ReadAsync(source, ct);
                    var csv = new CsvConnector(table, mapping.Csv ?? CsvColumns.Propose(table.Headers), ImportService.CsvProjectName, clock);
                    await csv.WriteSnapshotAsync(file, scope, null, includeFiles: false, clock, ct);
                }
                else
                {
                    using var export = await KuproExportConnector.OpenAsync(source, ct);
                    await export.WriteSnapshotAsync(file, scope, null, includeFiles: true, clock, ct);
                }
            }

            await snapshots.StoreAsync(import.WorkspaceId, import.Id, path, ct);
        }
        finally
        {
            File.Delete(path);
        }

        import.SourceReadAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import run {RunId} ended, but the email to its owner failed")]
    private static partial void LogNotifyFailed(ILogger logger, Guid runId, Exception exception);
}

/// <summary>The import's emails to its owner, EN and DA (ADR-019 Email, docs/branding.md glossary).</summary>
public static class ImportMail
{
    public static EmailMessage For(string to, string locale, string workspace, string source, ImportRunState state, int issues, string? error)
    {
        var danish = locale.StartsWith("da", StringComparison.OrdinalIgnoreCase);
        return (state, danish) switch
        {
            (ImportRunState.Succeeded, true) => new EmailMessage(to, $"Importen fra {source} til {workspace} er færdig",
                $"{issues} opgaver kom med. Rapporten ligger under Indstillinger → Import i Kupro."),
            (ImportRunState.Succeeded, false) => new EmailMessage(to, $"Your import from {source} into {workspace} is done",
                $"{issues} issues came across. The report is under Settings → Import in Kupro."),
            (_, true) => new EmailMessage(to, $"Importen fra {source} til {workspace} stoppede",
                $"{error} Kupro beholder det, der nåede at komme med. Kør importen igen for at fortsætte."),
            _ => new EmailMessage(to, $"Your import from {source} into {workspace} stopped",
                $"{error} Kupro keeps what it finished. Run the import again to continue."),
        };
    }

    public static EmailMessage SyncOff(string to, string locale, string workspace, string source) =>
        locale.StartsWith("da", StringComparison.OrdinalIgnoreCase)
            ? new EmailMessage(to, $"Automatisk synkronisering fra {source} til {workspace} er slået fra",
                "Hverken kilden eller de importerede opgaver har ændret sig i 30 dage. Slå den til igen under Indstillinger → Import, eller afslut importen.")
            : new EmailMessage(to, $"Auto-sync from {source} into {workspace} is off",
                "Neither the source nor the imported issues changed for 30 days. Turn it back on under Settings → Import, or finish the import.");
}
