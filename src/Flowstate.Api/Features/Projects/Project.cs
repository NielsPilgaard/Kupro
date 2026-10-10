using Flowstate.Api.Features.Workspaces;
using Flowstate.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Flowstate.Api.Features.Projects;

/// <summary>Named project colours; the SPA maps each to a design token (no hex in the API).</summary>
public enum ProjectColor
{
    Verdigris,
    Copper,
    Plum,
    Moss,
    Ochre,
    Brick,
    Slate,
    Fjord,
}

public sealed class Project : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }

    /// <summary>Issue key prefix, e.g. <c>FLOW</c> in <c>FLOW-123</c>. Unique per workspace; frozen once issues exist.</summary>
    public required string Key { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }
    public ProjectColor Color { get; set; }

    /// <summary>Last issue number handed out; the next issue gets <c>IssueCounter + 1</c>.</summary>
    public int IssueCounter { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.HasOne<Workspace>().WithMany().HasForeignKey(p => p.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => new { p.WorkspaceId, p.Key }).IsUnique();
        builder.Property(p => p.Key).HasMaxLength(10);
        builder.Property(p => p.Name).HasMaxLength(100);
        builder.Property(p => p.Description).HasMaxLength(2000);
        builder.Property(p => p.Color).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.IssueCounter).IsConcurrencyToken();
    }
}
