using Kupro.Api.Features.Users;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kupro.Api.Features.Workspaces;

/// <summary>The tenant and billing unit (ADR-009, ADR-013).</summary>
public sealed class Workspace
{
    public Guid Id { get; set; }
    public required string Slug { get; set; }
    public required string Name { get; set; }

    /// <summary>Capacity per person per week for the delivery plan (ADR-008). Default 37, Danish full time.</summary>
    public decimal HoursPerWeek { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public enum MemberKind
{
    Human,
    Agent,
}

/// <summary>Ordered by power, so <c>role &gt;= Member</c> reads naturally.</summary>
public enum WorkspaceRole
{
    Guest,
    Member,
    Owner,
}

public enum MemberStatus
{
    Invited,
    Active,
    Deactivated,
}

/// <summary>
/// A human or agent in a workspace. Never deleted: deactivation keeps history and attribution (ADR-013).
/// Agents act on behalf of <see cref="UserId"/>, their owner.
/// </summary>
public sealed class Member : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public MemberKind Kind { get; set; }

    /// <summary>Human: the user. Agent: the owning user. Null only for a pending invite.</summary>
    public Guid? UserId { get; set; }

    public string? AgentName { get; set; }

    /// <summary>Lower-cased email the invite was sent to.</summary>
    public string? InviteEmail { get; set; }

    public WorkspaceRole Role { get; set; }

    /// <summary>Billing access on top of the role (ADR-013). Humans only; Owners have it regardless.</summary>
    public bool CanManageBilling { get; set; }

    public MemberStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? JoinedAt { get; set; }
    public DateTimeOffset? DeactivatedAt { get; set; }
}

internal sealed class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> builder)
    {
        builder.HasIndex(w => w.Slug).IsUnique();
        builder.Property(w => w.Slug).HasMaxLength(40);
        builder.Property(w => w.Name).HasMaxLength(100);
        builder.Property(w => w.HoursPerWeek).HasPrecision(5, 2);
    }
}

internal sealed class MemberConfiguration : IEntityTypeConfiguration<Member>
{
    public void Configure(EntityTypeBuilder<Member> builder)
    {
        builder.HasOne<Workspace>().WithMany().HasForeignKey(m => m.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(m => m.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(m => m.Role).HasConversion<string>().HasMaxLength(16);
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(m => m.AgentName).HasMaxLength(64);
        builder.Property(m => m.InviteEmail).HasMaxLength(320);

        // Seat count (ADR-013 IMP-003); workspace_id first, as in every tenant index (ADR-009).
        builder.HasIndex(m => new { m.WorkspaceId, m.Kind, m.Status });
        builder.HasIndex(m => new { m.WorkspaceId, m.UserId }).IsUnique().HasFilter("kind = 'Human' AND user_id IS NOT NULL");
        builder.HasIndex(m => new { m.WorkspaceId, m.AgentName }).IsUnique().HasFilter("kind = 'Agent'");
        builder.HasIndex(m => new { m.WorkspaceId, m.InviteEmail }).IsUnique().HasFilter("status = 'Invited'");
        builder.HasIndex(m => m.UserId);
        builder.HasIndex(m => m.InviteEmail).HasFilter("status = 'Invited'");
    }
}
