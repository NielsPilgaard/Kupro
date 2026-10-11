using Flowstate.Api.Data;
using Flowstate.Api.Features.Issues;
using Flowstate.Api.Features.Workspaces;
using Flowstate.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Flowstate.Api.Features.Attachments;

/// <summary>
/// Attachments (task 008): create (returns a presigned upload URL), complete (checks the object landed), get (returns
/// a short-lived download URL). Owns Attachment. The tenant filter makes other workspaces' attachments not found.
/// </summary>
public sealed class AttachmentService(
    IDbContextFactory<FlowstateDbContext> dbFactory,
    WorkspaceContext workspace,
    ObjectStorage storage,
    TimeProvider clock)
{
    public const long MaxSize = 25 * 1024 * 1024;
    private static readonly TimeSpan UploadWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DownloadWindow = TimeSpan.FromMinutes(5);

    public async Task<IssueResult<AttachmentUpload>> CreateAsync(CreateAttachmentRequest request, CancellationToken ct)
    {
        if (!storage.Enabled)
        {
            return Unavailable;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        Guid? issueId = null;
        if (request.Issue is { } key)
        {
            var normalized = key.Trim().ToUpperInvariant();
            issueId = await db.Issues.Where(i => i.Key == normalized).Select(i => (Guid?)i.Id).SingleOrDefaultAsync(ct);
            if (issueId is null)
            {
                return IssueError.NotFound(normalized);
            }
        }

        var attachment = new Attachment
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.RequireWorkspaceId(),
            IssueId = issueId,
            UploadedById = workspace.MemberId,
            FileName = Path.GetFileName(request.FileName.Trim()),
            ContentType = request.ContentType.ToLowerInvariant(),
            Size = request.Size,
            CreatedAt = clock.GetUtcNow(),
        };
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync(ct);

        var expires = clock.GetUtcNow() + UploadWindow;
        var url = await storage.PresignUploadAsync(ObjectKey(attachment), attachment.ContentType, expires);
        return new AttachmentUpload((await GetAsync(db, attachment.Id, ct))!, url, expires);
    }

    /// <summary>Marks the upload ready once the object exists and is within the size limit.</summary>
    public async Task<IssueResult<AttachmentResponse>> CompleteAsync(Guid id, CancellationToken ct)
    {
        if (!storage.Enabled)
        {
            return Unavailable;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var attachment = await db.Attachments.SingleOrDefaultAsync(a => a.Id == id, ct);
        if (attachment is null)
        {
            return NotFound(id);
        }

        if (attachment.UploadedById != workspace.MemberId)
        {
            return new IssueError("not_uploader", "Only the uploader can complete an upload.");
        }

        if (!attachment.Ready)
        {
            var size = await storage.SizeAsync(ObjectKey(attachment), ct);
            if (size is null)
            {
                return new IssueError("upload_missing", "Nothing was uploaded yet. PUT the file to the upload URL first.");
            }

            if (size > MaxSize)
            {
                await storage.DeleteAsync(ObjectKey(attachment), ct);
                return new IssueError("attachment_too_large", $"Attachments are at most {MaxSize / (1024 * 1024)} MB.");
            }

            attachment.Size = size.Value;
            attachment.Ready = true;
            await db.SaveChangesAsync(ct);
        }

        return await WithDownloadAsync((await GetAsync(db, id, ct))!);
    }

    public async Task<IssueResult<AttachmentResponse>> GetAsync(Guid id, CancellationToken ct)
    {
        if (!storage.Enabled)
        {
            return Unavailable;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await GetAsync(db, id, ct) is { } attachment ? await WithDownloadAsync(attachment) : NotFound(id);
    }

    private async Task<AttachmentResponse> WithDownloadAsync(AttachmentResponse a) =>
        a.Ready
            ? a with
            {
                DownloadUrl = await storage.PresignDownloadAsync(
                    $"{workspace.RequireWorkspaceId()}/{a.Id}", a.FileName, a.ContentType, clock.GetUtcNow() + DownloadWindow),
            }
            : a;

    private static string ObjectKey(Attachment a) => $"{a.WorkspaceId}/{a.Id}";

    private static IssueError Unavailable => new("attachments_unavailable", "File storage is not configured on this server.");

    private static IssueError NotFound(Guid id) => new("attachment_not_found", $"No attachment {id}.");

    private static async Task<AttachmentResponse?> GetAsync(FlowstateDbContext db, Guid id, CancellationToken ct) =>
        await db.Attachments.AsNoTracking().Where(a => a.Id == id)
            .Select(a => new AttachmentResponse(
                a.Id,
                a.FileName,
                a.ContentType,
                a.Size,
                db.Issues.Where(i => i.Id == a.IssueId).Select(i => i.Key).FirstOrDefault(),
                a.Ready,
                db.Members.Where(m => m.Id == a.UploadedById)
                    .Select(m => new MemberRef(
                        m.Id,
                        m.Kind == MemberKind.Agent
                            ? m.AgentName!
                            : db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefault()!,
                        m.Kind))
                    .First(),
                a.CreatedAt,
                "",
                null))
            .SingleOrDefaultAsync(ct) is { } found
            ? found with { Markdown = MarkdownFor(found) }
            : null;

    private static string MarkdownFor(AttachmentResponse a) =>
        $"{(a.ContentType.StartsWith("image/", StringComparison.Ordinal) ? "!" : "")}"
        + $"[{a.FileName.Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal)}](attachment:{a.Id})";
}
