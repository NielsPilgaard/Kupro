using System.ComponentModel.DataAnnotations;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kupro.Api.Features.Attachments;

/// <summary>
/// A file in object storage (ADR-004), uploaded by the client straight to storage with a presigned URL (task 008).
/// Markdown references it as <c>attachment:ID</c>; reading goes through the API, so only workspace members get a
/// download URL.
/// </summary>
public sealed class Attachment : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }

    /// <summary>Issue it was uploaded for; null when pasted before the issue existed.</summary>
    public Guid? IssueId { get; set; }

    public Guid UploadedById { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }

    /// <summary>False until the client confirms the upload and the API has seen the object.</summary>
    public bool Ready { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.HasOne<Workspace>().WithMany().HasForeignKey(a => a.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Issue>().WithMany().HasWorkspaceForeignKey(a => a.IssueId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Member>().WithMany().HasWorkspaceForeignKey(a => a.UploadedById).OnDelete(DeleteBehavior.Restrict);
        builder.Property(a => a.FileName).HasMaxLength(200);
        builder.Property(a => a.ContentType).HasMaxLength(100);
        builder.HasIndex(a => new { a.WorkspaceId, a.IssueId });
    }
}

/// <param name="FileName">Original file name, shown on download.</param>
/// <param name="ContentType">MIME type; the upload must send the same <c>Content-Type</c>.</param>
/// <param name="Size">Bytes, at most 25 MB.</param>
/// <param name="Issue">Issue key it belongs to, if any.</param>
public sealed record CreateAttachmentRequest(
    [Required, StringLength(200, MinimumLength = 1)] string FileName,
    [Required, StringLength(100), RegularExpression(@"^[\w.+-]+/[\w.+-]+$")] string ContentType,
    [Range(1, AttachmentService.MaxSize)] long Size,
    [StringLength(24)] string? Issue = null);

/// <param name="Markdown">Ready-to-paste reference: <c>![name](attachment:ID)</c> for images, a link otherwise.</param>
/// <param name="DownloadUrl">Short-lived URL (5 minutes) for a ready attachment.</param>
public sealed record AttachmentResponse(
    Guid Id,
    string FileName,
    string ContentType,
    long Size,
    string? IssueKey,
    bool Ready,
    MemberRef UploadedBy,
    DateTimeOffset CreatedAt,
    string Markdown,
    string? DownloadUrl);

/// <param name="UploadUrl">PUT the file body here with header <c>Content-Type</c> equal to the declared type, then call complete.</param>
/// <param name="ExpiresAt">The upload URL stops working after this.</param>
public sealed record AttachmentUpload(AttachmentResponse Attachment, string UploadUrl, DateTimeOffset ExpiresAt);
