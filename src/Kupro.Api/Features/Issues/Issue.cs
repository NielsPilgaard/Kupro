using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Sprints;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;

namespace Kupro.Api.Features.Issues;

/// <summary>Strict hierarchy: Epic ← Feature ← Task; a Task may also sit directly under an Epic (ADR-008).</summary>
public enum IssueType
{
    Task,
    Feature,
    Epic,
}

/// <summary>Fixed workflow (ADR-008). Stored as its ordinal, so board columns sort naturally.</summary>
public enum IssueState
{
    Backlog,
    Todo,
    InProgress,
    InReview,
    Done,
    Canceled,
}

public enum IssuePriority
{
    None,
    Low,
    Medium,
    High,
    Urgent,
}

/// <summary>Stored from the source issue's side; "blocked by" is the inverse view of <see cref="Blocks"/>.</summary>
public enum RelationType
{
    Blocks,
    Relates,
    Duplicates,
}

public sealed class Issue : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public int Number { get; set; }

    /// <summary><c>KUP-123</c>. Stable: project keys freeze once a project has issues.</summary>
    public required string Key { get; set; }

    public IssueType Type { get; set; }
    public required string Title { get; set; }

    /// <summary>Markdown.</summary>
    public string? Description { get; set; }

    public IssueState State { get; set; }
    public IssuePriority Priority { get; set; }
    public Guid? AssigneeId { get; set; }
    public Guid? ParentId { get; set; }

    /// <summary>At most one sprint (ADR-008).</summary>
    public Guid? SprintId { get; set; }

    /// <summary>Hours (ADR-008 OQ-1).</summary>
    public decimal? Effort { get; set; }

    public DateOnly? StartDate { get; set; }
    public DateOnly? DueDate { get; set; }

    /// <summary>Manual order within a board column; lower first. Fractional so a move touches one row.</summary>
    public double Rank { get; set; }

    public Guid CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the issue entered Done or Canceled; cleared when it leaves them.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    public List<IssueLabel> Labels { get; set; } = [];
}

/// <summary>Workspace-level label. <c>bug</c> is the built-in one (ADR-008 OQ-2); others are created on first use.</summary>
public sealed class Label : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }
    public ProjectColor Color { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class IssueLabel : ITenantOwned
{
    public Guid WorkspaceId { get; set; }
    public Guid IssueId { get; set; }
    public Guid LabelId { get; set; }
}

public sealed class IssueRelation : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SourceId { get; set; }
    public Guid TargetId { get; set; }
    public RelationType Type { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class IssueConfiguration : IEntityTypeConfiguration<Issue>
{
    public void Configure(EntityTypeBuilder<Issue> builder)
    {
        builder.HasOne<Workspace>().WithMany().HasForeignKey(i => i.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Project>().WithMany().HasWorkspaceForeignKey(i => i.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Member>().WithMany().HasWorkspaceForeignKey(i => i.AssigneeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Member>().WithMany().HasWorkspaceForeignKey(i => i.CreatedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Issue>().WithMany().HasWorkspaceForeignKey(i => i.ParentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Sprint>().WithMany().HasWorkspaceForeignKey(i => i.SprintId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(i => i.Labels).WithOne().HasWorkspaceForeignKey(l => l.IssueId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(i => i.Key).HasMaxLength(24);
        builder.Property(i => i.Title).HasMaxLength(500);
        builder.Property(i => i.Description).HasMaxLength(100_000);
        builder.Property(i => i.Type).HasConversion<string>().HasMaxLength(16);
        builder.Property(i => i.State).HasConversion<short>();
        builder.Property(i => i.Priority).HasConversion<short>();
        builder.Property(i => i.Effort).HasPrecision(8, 2);

        // workspace_id leads every index (ADR-009); each list filter has one (ADR-010).
        builder.HasIndex(i => new { i.WorkspaceId, i.Key }).IsUnique();
        builder.HasIndex(i => new { i.ProjectId, i.Number }).IsUnique();
        builder.HasIndex(i => new { i.WorkspaceId, i.ProjectId, i.State, i.Rank });
        builder.HasIndex(i => new { i.WorkspaceId, i.UpdatedAt, i.Id });
        builder.HasIndex(i => new { i.WorkspaceId, i.AssigneeId });
        builder.HasIndex(i => new { i.WorkspaceId, i.ParentId });
        builder.HasIndex(i => new { i.WorkspaceId, i.Type });
        builder.HasIndex(i => new { i.WorkspaceId, i.SprintId });

        // Full-text search (task 009): key and title weigh more than the description; titles and descriptions are
        // stemmed as English and Danish both. A shadow property, so the entity stays plain.
        builder.Property<NpgsqlTsVector>(SearchVector).HasComputedColumnSql(
            "setweight(to_tsvector('simple', key), 'A') || "
            + "setweight(to_tsvector('english', title), 'A') || setweight(to_tsvector('danish', title), 'A') || "
            + "setweight(to_tsvector('english', coalesce(description, '')), 'B') || "
            + "setweight(to_tsvector('danish', coalesce(description, '')), 'B')",
            stored: true);
        builder.HasIndex(SearchVector).HasMethod("GIN");
    }

    /// <summary>Name of the stored <c>tsvector</c> column, for <c>EF.Property</c>.</summary>
    public const string SearchVector = "SearchVector";
}

internal sealed class LabelConfiguration : IEntityTypeConfiguration<Label>
{
    public void Configure(EntityTypeBuilder<Label> builder)
    {
        builder.HasOne<Workspace>().WithMany().HasForeignKey(l => l.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(l => l.Name).HasMaxLength(50);
        builder.Property(l => l.Color).HasConversion<string>().HasMaxLength(16);
        builder.HasIndex(l => new { l.WorkspaceId, l.Name }).IsUnique();
    }
}

internal sealed class IssueLabelConfiguration : IEntityTypeConfiguration<IssueLabel>
{
    public void Configure(EntityTypeBuilder<IssueLabel> builder)
    {
        builder.HasKey(l => new { l.IssueId, l.LabelId });
        builder.HasOne<Label>().WithMany().HasWorkspaceForeignKey(l => l.LabelId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(l => new { l.WorkspaceId, l.LabelId });
    }
}

internal sealed class IssueRelationConfiguration : IEntityTypeConfiguration<IssueRelation>
{
    public void Configure(EntityTypeBuilder<IssueRelation> builder)
    {
        builder.HasOne<Issue>().WithMany().HasWorkspaceForeignKey(r => r.SourceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Issue>().WithMany().HasWorkspaceForeignKey(r => r.TargetId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(r => r.Type).HasConversion<string>().HasMaxLength(16);
        builder.HasIndex(r => new { r.SourceId, r.TargetId, r.Type }).IsUnique();
        builder.HasIndex(r => new { r.WorkspaceId, r.TargetId });
    }
}
