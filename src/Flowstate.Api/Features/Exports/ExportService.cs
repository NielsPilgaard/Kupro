using System.Threading.Channels;
using Flowstate.Api.Data;
using Flowstate.Api.Features.Attachments;
using Flowstate.Api.Features.Email;
using Flowstate.Api.Features.Issues;
using Flowstate.Api.Features.Meetings;
using Flowstate.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Flowstate.Api.Features.Exports;

/// <summary>
/// Workspace export (task 016): owners start one, <see cref="ExportJob"/> builds the ZIP in the background, and a
/// ready export gives a short-lived download link until it is deleted after <see cref="KeepFor"/>. Owns WorkspaceExport.
/// </summary>
public sealed class ExportService(
    IDbContextFactory<FlowstateDbContext> dbFactory,
    WorkspaceContext workspace,
    ObjectStorage storage,
    ExportSignal signal,
    TimeProvider clock)
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);
    private static readonly TimeSpan DownloadWindow = TimeSpan.FromMinutes(15);

    /// <summary>Starts an export, or returns the one already building: one at a time per workspace.</summary>
    public async Task<IssueResult<ExportResponse>> StartAsync(CancellationToken ct)
    {
        if (!storage.Enabled)
        {
            return Unavailable;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var running = await db.WorkspaceExports.Where(e => e.State == ExportState.Building).Select(e => (Guid?)e.Id).FirstOrDefaultAsync(ct);
        if (running is { } id)
        {
            return (await ResponsesAsync(db, db.WorkspaceExports.Where(e => e.Id == id), ct))[0];
        }

        var export = new WorkspaceExport
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.RequireWorkspaceId(),
            RequestedById = workspace.MemberId,
            State = ExportState.Building,
            CreatedAt = clock.GetUtcNow(),
        };
        db.WorkspaceExports.Add(export);
        await db.SaveChangesAsync(ct);
        signal.Signal();
        return (await ResponsesAsync(db, db.WorkspaceExports.Where(e => e.Id == export.Id), ct))[0];
    }

    /// <summary>Exports from the last <see cref="KeepFor"/>, newest first.</summary>
    public async Task<IssueResult<IReadOnlyList<ExportResponse>>> ListAsync(CancellationToken ct)
    {
        if (!storage.Enabled)
        {
            return Unavailable;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await ResponsesAsync(db, db.WorkspaceExports.OrderByDescending(e => e.CreatedAt).Take(20), ct);
    }

    public async Task<IssueResult<ExportResponse>> GetAsync(Guid id, CancellationToken ct)
    {
        if (!storage.Enabled)
        {
            return Unavailable;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var found = await ResponsesAsync(db, db.WorkspaceExports.Where(e => e.Id == id), ct);
        return found.Count == 1 ? found[0] : new IssueError("export_not_found", $"No export {id}.");
    }

    internal static string ObjectKey(Guid workspaceId, Guid exportId) => $"{workspaceId}/exports/{exportId}.zip";

    private async Task<List<ExportResponse>> ResponsesAsync(FlowstateDbContext db, IQueryable<WorkspaceExport> query, CancellationToken ct)
    {
        var rows = await query.AsNoTracking()
            .Select(e => new { Export = e, RequestedBy = db.Members.Where(m => m.Id == e.RequestedById).Select(MemberRefs.Projection(db)).First() })
            .ToListAsync(ct);
        var list = new List<ExportResponse>(rows.Count);
        foreach (var row in rows)
        {
            var e = row.Export;
            var url = e.State == ExportState.Ready
                ? await storage.PresignDownloadAsync(ObjectKey(e.WorkspaceId, e.Id), $"flowstate-{workspace.Slug}-{e.CreatedAt:yyyy-MM-dd}.zip",
                    "application/zip", clock.GetUtcNow() + DownloadWindow)
                : null;
            list.Add(new ExportResponse(e.Id, e.State, row.RequestedBy, e.Size, e.CreatedAt, e.CompletedAt, e.CreatedAt + KeepFor, url));
        }

        return list;
    }

    private static IssueError Unavailable => new("exports_unavailable", "File storage is not configured on this server.");
}

/// <summary>Wakes <see cref="ExportJob"/> when an export is started. Singleton.</summary>
public sealed class ExportSignal
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

/// <summary>
/// Builds exports that are waiting (also after a restart) and deletes expired ones. One export at a time, so a big
/// workspace doesn't starve the API of database connections.
/// </summary>
public sealed partial class ExportJob(
    IServiceScopeFactory scopes, ObjectStorage storage, ExportSignal signal, TimeProvider clock, ILogger<ExportJob> logger) : BackgroundService
{
    private DateTimeOffset _lastPurge;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!storage.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await BuildWaitingAsync(stoppingToken);
                if (clock.GetUtcNow() - _lastPurge > TimeSpan.FromHours(1))
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await using var db = await DbAsync(scope, stoppingToken);
                    await PurgeExpiredAsync(db, storage, clock.GetUtcNow(), stoppingToken);
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

    /// <summary>Deletes exports older than <see cref="ExportService.KeepFor"/>: the file, then the row.</summary>
    public static async Task<int> PurgeExpiredAsync(FlowstateDbContext db, ObjectStorage storage, DateTimeOffset now, CancellationToken ct)
    {
        // System job across all tenants: no request workspace exists.
        var expired = await db.WorkspaceExports.IgnoreQueryFilters([FlowstateDbContext.TenantFilter])
            .Where(e => e.CreatedAt < now - ExportService.KeepFor).Select(e => new { e.Id, e.WorkspaceId }).ToListAsync(ct);
        foreach (var e in expired)
        {
            await storage.DeleteAsync(ExportService.ObjectKey(e.WorkspaceId, e.Id), ct);
            await db.WorkspaceExports.IgnoreQueryFilters([FlowstateDbContext.TenantFilter]).Where(x => x.Id == e.Id).ExecuteDeleteAsync(ct);
        }

        return expired.Count;
    }

    private async Task BuildWaitingAsync(CancellationToken ct)
    {
        List<(Guid Id, Guid WorkspaceId)> waiting;
        await using (var scope = scopes.CreateAsyncScope())
        await using (var db = await DbAsync(scope, ct))
        {
            // System job across all tenants: finds every workspace's waiting exports; each is then built inside its own workspace.
            waiting = (await db.WorkspaceExports.IgnoreQueryFilters([FlowstateDbContext.TenantFilter])
                    .Where(e => e.State == ExportState.Building).OrderBy(e => e.CreatedAt)
                    .Select(e => new { e.Id, e.WorkspaceId }).ToListAsync(ct))
                .Select(e => (e.Id, e.WorkspaceId)).ToList();
        }

        foreach (var (id, workspaceId) in waiting)
        {
            await BuildAsync(id, workspaceId, ct);
        }
    }

    private async Task BuildAsync(Guid id, Guid workspaceId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<WorkspaceContext>().SetForSystem(workspaceId);
        await using var db = await DbAsync(scope, ct);
        var export = await db.WorkspaceExports.SingleAsync(e => e.Id == id, ct);
        var path = Path.GetTempFileName();
        try
        {
            await using (var file = File.Create(path))
            {
                await ExportWriter.WriteAsync(file, db, storage, clock.GetUtcNow(), ct);
            }

            await storage.UploadFileAsync(ExportService.ObjectKey(workspaceId, id), path, "application/zip", ct);
            export.State = ExportState.Ready;
            export.Size = new FileInfo(path).Length;
            export.CompletedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogBuildFailed(logger, id, ex);
            export.State = ExportState.Failed;
            export.CompletedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }
        finally
        {
            File.Delete(path);
        }

        // The export is ready either way; Settings shows it even when the email doesn't arrive.
        try
        {
            await NotifyAsync(scope, db, export, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogNotifyFailed(logger, id, ex);
        }
    }

    /// <summary>Tells the owner who asked, in their language, where to download it.</summary>
    private static async Task NotifyAsync(AsyncServiceScope scope, FlowstateDbContext db, WorkspaceExport export, CancellationToken ct)
    {
        var to = await db.Members.Where(m => m.Id == export.RequestedById)
            .Select(m => db.Users.Where(u => u.Id == m.UserId).Select(u => new { u.Email, u.Locale }).FirstOrDefault())
            .SingleOrDefaultAsync(ct);
        if (to is null)
        {
            return;
        }

        var name = await db.Workspaces.Where(w => w.Id == export.WorkspaceId).Select(w => w.Name).SingleAsync(ct);
        var days = ExportService.KeepFor.Days;
        var message = to.Locale.StartsWith("da", StringComparison.OrdinalIgnoreCase)
            ? new EmailMessage(to.Email, $"Eksporten af {name} er klar",
                $"Hent den under Indstillinger i Flowstate. Filen slettes efter {days} dage.")
            : new EmailMessage(to.Email, $"Your export of {name} is ready",
                $"Download it from Settings in Flowstate. The file is deleted after {days} days.");
        await scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(message, ct);
    }

    private static Task<FlowstateDbContext> DbAsync(AsyncServiceScope scope, CancellationToken ct) =>
        scope.ServiceProvider.GetRequiredService<IDbContextFactory<FlowstateDbContext>>().CreateDbContextAsync(ct);

    [LoggerMessage(Level = LogLevel.Error, Message = "Export run failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Export {ExportId} failed")]
    private static partial void LogBuildFailed(ILogger logger, Guid exportId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Export {ExportId} is ready, but the email to its owner failed")]
    private static partial void LogNotifyFailed(ILogger logger, Guid exportId, Exception exception);
}
