using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kupro.Api.Features.Plan;

/// <summary>Dated marker on the delivery plan; no project means workspace-wide (ADR-008 Planning).</summary>
public sealed class Milestone : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    public required string Name { get; set; }
    public DateOnly Date { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>People on a project from a month onward; the latest entry at or before a month applies (step function).</summary>
public sealed class ProjectCapacity : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>First day of the month the headcount applies from.</summary>
    public DateOnly FromMonth { get; set; }

    public decimal People { get; set; }
}

internal sealed class MilestoneConfiguration : IEntityTypeConfiguration<Milestone>
{
    public void Configure(EntityTypeBuilder<Milestone> builder)
    {
        builder.HasOne<Workspace>().WithMany().HasForeignKey(m => m.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Project>().WithMany().HasWorkspaceForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(m => m.Name).HasMaxLength(100);
        builder.HasIndex(m => new { m.WorkspaceId, m.Date });
    }
}

internal sealed class ProjectCapacityConfiguration : IEntityTypeConfiguration<ProjectCapacity>
{
    public void Configure(EntityTypeBuilder<ProjectCapacity> builder)
    {
        builder.ToTable("project_capacity");
        builder.HasOne<Workspace>().WithMany().HasForeignKey(c => c.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Project>().WithMany().HasWorkspaceForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(c => c.People).HasPrecision(6, 2);
        builder.HasIndex(c => new { c.ProjectId, c.FromMonth }).IsUnique();
        builder.HasIndex(c => new { c.WorkspaceId, c.ProjectId });
    }
}
