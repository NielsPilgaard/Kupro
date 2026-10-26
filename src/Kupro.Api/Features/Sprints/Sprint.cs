using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kupro.Api.Features.Sprints;

public enum SprintState
{
    Planned,
    Active,
    Closed,
}

/// <summary>Per-project timebox with fixed dates; one active per project (ADR-008).</summary>
public sealed class Sprint : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public string? Goal { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public SprintState State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
}

internal sealed class SprintConfiguration : IEntityTypeConfiguration<Sprint>
{
    public void Configure(EntityTypeBuilder<Sprint> builder)
    {
        builder.HasOne<Workspace>().WithMany().HasForeignKey(s => s.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Project>().WithMany().HasWorkspaceForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(s => s.Name).HasMaxLength(100);
        builder.Property(s => s.Goal).HasMaxLength(1000);
        builder.Property(s => s.State).HasConversion<string>().HasMaxLength(16);
        builder.HasIndex(s => new { s.WorkspaceId, s.ProjectId, s.StartDate });
        // One active sprint per project, enforced by the database.
        builder.HasIndex(s => s.ProjectId).IsUnique().HasFilter("state = 'Active'").HasDatabaseName("ix_sprints_one_active_per_project");
    }
}
