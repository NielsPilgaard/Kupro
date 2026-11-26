using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kupro.Api.Features.Time;

/// <summary>
/// Time a member spent on an issue on one day (ADR-021). A day's working time is the sum of that day's entries. Locked
/// while the issue is Done or Canceled; the lock is read from the issue, not stored here (ADR-021 IMP-003).
/// </summary>
public sealed class TimeEntry : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }

    /// <summary>The person whose time it is: a human member or an import's placeholder, never a guest or an agent.</summary>
    public Guid MemberId { get; set; }

    public Guid IssueId { get; set; }

    /// <summary>Calendar day in Europe/Copenhagen.</summary>
    public DateOnly Date { get; set; }

    public int Minutes { get; set; }
    public string? Note { get; set; }
    public Surface Via { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum TimeChangeKind
{
    Created,
    Updated,
    Deleted,
}

/// <summary>
/// Append-only history of every create, edit and delete of a time entry (ADR-021 Reliability). Kept when the entry is
/// deleted, so it has no foreign key to the entry.
/// </summary>
public sealed class TimeEntryChange : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid EntryId { get; set; }

    /// <summary>Whose entry it is.</summary>
    public Guid MemberId { get; set; }

    public Guid IssueId { get; set; }

    /// <summary>Who made the change: the person, or the agent acting for them (ADR-001 attribution).</summary>
    public Guid ChangedById { get; set; }

    public Surface Via { get; set; }
    public TimeChangeKind Kind { get; set; }
    public DateTimeOffset At { get; set; }
    public DateOnly? OldDate { get; set; }
    public DateOnly? NewDate { get; set; }
    public int? OldMinutes { get; set; }
    public int? NewMinutes { get; set; }
    public string? OldNote { get; set; }
    public string? NewNote { get; set; }
}

internal sealed class TimeEntryConfiguration : IEntityTypeConfiguration<TimeEntry>
{
    public void Configure(EntityTypeBuilder<TimeEntry> builder)
    {
        builder.ToTable("time_entries");
        builder.HasOne<Issue>().WithMany().HasWorkspaceForeignKey(e => e.IssueId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Member>().WithMany().HasWorkspaceForeignKey(e => e.MemberId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(e => e.Via).HasConversion<string>().HasMaxLength(8);
        builder.Property(e => e.Note).HasMaxLength(TimeLimits.MaxNote);

        // A week's timesheet and the export are one range scan each (ADR-010); an issue's time is the other read.
        builder.HasIndex(e => new { e.WorkspaceId, e.MemberId, e.Date });
        builder.HasIndex(e => new { e.WorkspaceId, e.IssueId });
        builder.HasIndex(e => new { e.WorkspaceId, e.Date });
    }
}

internal sealed class TimeEntryChangeConfiguration : IEntityTypeConfiguration<TimeEntryChange>
{
    public void Configure(EntityTypeBuilder<TimeEntryChange> builder)
    {
        builder.ToTable("time_entry_changes");
        builder.HasOne<Issue>().WithMany().HasWorkspaceForeignKey(c => c.IssueId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Member>().WithMany().HasWorkspaceForeignKey(c => c.MemberId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Member>().WithMany().HasWorkspaceForeignKey(c => c.ChangedById).OnDelete(DeleteBehavior.Restrict);
        builder.Property(c => c.Via).HasConversion<string>().HasMaxLength(8);
        builder.Property(c => c.Kind).HasConversion<string>().HasMaxLength(8);
        builder.Property(c => c.OldNote).HasMaxLength(TimeLimits.MaxNote);
        builder.Property(c => c.NewNote).HasMaxLength(TimeLimits.MaxNote);
        builder.HasIndex(c => new { c.WorkspaceId, c.MemberId, c.At });
        builder.HasIndex(c => new { c.WorkspaceId, c.EntryId });
    }
}

public static class TimeLimits
{
    public const int MaxNote = 2000;

    /// <summary>A day, the most one entry or a member's day can hold when logged in Kupro (ADR-021 Model).</summary>
    public const int MinutesPerDay = 1440;

    /// <summary>Longest range a member's list or an Owner's list answers in one call.</summary>
    public const int MaxListDays = 93;

    /// <summary>Longest range one export covers.</summary>
    public const int MaxExportDays = 366;

    /// <summary>Entries and their history are kept this many years after the day they record (ADR-021 Retention).</summary>
    public const int RetentionYears = 5;
}
