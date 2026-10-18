using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kupro.Api.Features.Exports;

public enum ExportState
{
    Building,
    Ready,
    Failed,
}

/// <summary>
/// One ZIP of a whole workspace (task 016), built in the background and kept in object storage for
/// <see cref="ExportService.KeepFor"/>; then the file and this row are deleted.
/// </summary>
public sealed class WorkspaceExport : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid RequestedById { get; set; }
    public ExportState State { get; set; }

    /// <summary>ZIP size in bytes once ready.</summary>
    public long? Size { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

internal sealed class WorkspaceExportConfiguration : IEntityTypeConfiguration<WorkspaceExport>
{
    public void Configure(EntityTypeBuilder<WorkspaceExport> builder)
    {
        builder.ToTable("workspace_exports");
        builder.HasOne<Workspace>().WithMany().HasForeignKey(e => e.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Member>().WithMany().HasForeignKey(e => e.RequestedById).OnDelete(DeleteBehavior.Restrict);
        builder.Property(e => e.State).HasConversion<string>().HasMaxLength(16);
        builder.HasIndex(e => new { e.WorkspaceId, e.CreatedAt });
        builder.HasIndex(e => e.State);
    }
}

/// <param name="Size">ZIP size in bytes, once ready.</param>
/// <param name="DownloadUrl">Ready exports only: a link to the ZIP, valid for 15 minutes. Ask again for a new one.</param>
/// <param name="ExpiresAt">When the file is deleted.</param>
public sealed record ExportResponse(
    Guid Id,
    ExportState State,
    MemberRef RequestedBy,
    long? Size,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset ExpiresAt,
    string? DownloadUrl);
