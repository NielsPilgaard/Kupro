using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kupro.Api.Features.Meetings;

public enum MeetingType
{
    Standup,
    Planning,
    Refinement,
    Other,
}

/// <summary>Suggest: every proposal waits for approval. Full auto: safe proposals apply at once, with undo (task 015).</summary>
public enum MeetingMode
{
    Suggest,
    FullAuto,
}

public enum MeetingState
{
    Live,
    Paused,
    Ended,
}

/// <summary>What a proposal does; the same operations the MCP tools offer.</summary>
public enum ProposalOperation
{
    State,
    Sprint,
    Assignee,
    Estimate,
    Comment,
    CreateIssue,
}

public enum ProposalStatus
{
    Pending,
    Applied,
    Rejected,
    Undone,
}

/// <summary>A meeting the agent listens to, in one project (task 015, ADR-015).</summary>
public sealed class MeetingSession : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public MeetingType Type { get; set; }
    public MeetingMode Mode { get; set; }

    /// <summary>Spoken language, <c>en</c> or <c>da</c>; picks the transcription and extraction models (ADR-015).</summary>
    public required string Language { get; set; }

    public MeetingState State { get; set; }
    public Guid StartedById { get; set; }

    /// <summary>The <c>meeting-agent</c> member, owned by whoever started the meeting; the actor of applied changes.</summary>
    public Guid AgentId { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

/// <summary>One utterance: transcribed audio or text sent by a client. Deleted after the workspace's retention.</summary>
public sealed class TranscriptSegment : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SessionId { get; set; }

    /// <summary>Member whose client sent the audio or text.</summary>
    public Guid SpeakerId { get; set; }

    public required string Text { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A ticket action the agent heard. Values are canonical: state name (<c>inProgress</c>), sprint id or <c>none</c>,
/// member id or <c>none</c>, hours, comment markdown, or the new issue's title.
/// </summary>
public sealed class MeetingProposal : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SessionId { get; set; }
    public Guid? SegmentId { get; set; }

    /// <summary>Target issue; null while the reference is ambiguous, and for <see cref="ProposalOperation.CreateIssue"/> until applied.</summary>
    public Guid? IssueId { get; set; }

    /// <summary>Issue keys that matched a vague reference ("the login bug"); the user picks one.</summary>
    public List<string> Candidates { get; set; } = [];

    public ProposalOperation Operation { get; set; }
    public string? Value { get; set; }

    /// <summary>The field's value when proposed, and again when applied (what undo restores).</summary>
    public string? Before { get; set; }

    public double Confidence { get; set; }

    /// <summary>Transcript words that triggered the proposal; cleared with the transcript.</summary>
    public string? Snippet { get; set; }

    public ProposalStatus Status { get; set; }
    public bool Edited { get; set; }
    public bool AutoApplied { get; set; }
    public Guid? ReviewedById { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }

    /// <summary>Comment written by a <see cref="ProposalOperation.Comment"/> proposal, so undo can delete it.</summary>
    public Guid? CommentId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Per-workspace meeting agent settings. Only owners enable full auto.</summary>
public sealed class MeetingSettings : ITenantOwned
{
    public Guid WorkspaceId { get; set; }
    public MeetingMode DefaultMode { get; set; }
    public bool FullAutoEnabled { get; set; }

    /// <summary>Operations full auto may apply without approval; others wait even in full auto.</summary>
    public List<ProposalOperation> FullAutoOperations { get; set; } = [];

    /// <summary>Days to keep transcripts and snippets. 0 (the default): delete them when the meeting ends.</summary>
    public int TranscriptRetentionDays { get; set; }
}

internal sealed class MeetingConfiguration :
    IEntityTypeConfiguration<MeetingSession>, IEntityTypeConfiguration<TranscriptSegment>,
    IEntityTypeConfiguration<MeetingProposal>, IEntityTypeConfiguration<MeetingSettings>
{
    public void Configure(EntityTypeBuilder<MeetingSession> builder)
    {
        builder.ToTable("meetings");
        builder.HasOne<Project>().WithMany().HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Member>().WithMany().HasForeignKey(m => m.StartedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Member>().WithMany().HasForeignKey(m => m.AgentId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(m => m.Type).HasConversion<string>().HasMaxLength(16);
        builder.Property(m => m.Mode).HasConversion<string>().HasMaxLength(16);
        builder.Property(m => m.State).HasConversion<string>().HasMaxLength(16);
        builder.Property(m => m.Language).HasMaxLength(8);
        builder.HasIndex(m => new { m.WorkspaceId, m.StartedAt });
    }

    public void Configure(EntityTypeBuilder<TranscriptSegment> builder)
    {
        builder.ToTable("transcript_segments");
        builder.HasOne<MeetingSession>().WithMany().HasForeignKey(t => t.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(t => t.Text).HasMaxLength(4000);
        builder.HasIndex(t => new { t.SessionId, t.CreatedAt });
        builder.HasIndex(t => t.CreatedAt);
    }

    public void Configure(EntityTypeBuilder<MeetingProposal> builder)
    {
        builder.ToTable("meeting_proposals");
        builder.HasOne<MeetingSession>().WithMany().HasForeignKey(p => p.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<TranscriptSegment>().WithMany().HasForeignKey(p => p.SegmentId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Issue>().WithMany().HasForeignKey(p => p.IssueId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(p => p.Operation).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.Value).HasMaxLength(4000);
        builder.Property(p => p.Before).HasMaxLength(4000);
        builder.Property(p => p.Snippet).HasMaxLength(1000);
        builder.HasIndex(p => new { p.SessionId, p.CreatedAt });
        builder.HasIndex(p => new { p.WorkspaceId, p.Operation });
    }

    public void Configure(EntityTypeBuilder<MeetingSettings> builder)
    {
        builder.ToTable("meeting_settings");
        builder.HasKey(s => s.WorkspaceId);
        builder.HasOne<Workspace>().WithOne().HasForeignKey<MeetingSettings>(s => s.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(s => s.DefaultMode).HasConversion<string>().HasMaxLength(16);
        builder.PrimitiveCollection(s => s.FullAutoOperations).ElementType().HasConversion<string>();
    }
}
