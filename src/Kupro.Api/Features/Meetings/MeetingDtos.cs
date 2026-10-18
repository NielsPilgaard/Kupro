using System.ComponentModel.DataAnnotations;
using Kupro.Api.Features.Issues;

namespace Kupro.Api.Features.Meetings;

/// <param name="Project">Project key; proposals target this project's issues.</param>
/// <param name="Type">Kind of meeting; helps the agent read intent.</param>
/// <param name="Mode">Default: the workspace's default mode. Full auto needs an admin to enable it first.</param>
/// <param name="Language">Spoken language: <c>en</c> or <c>da</c>.</param>
public sealed record StartMeetingRequest(
    [Required, StringLength(10)] string Project,
    MeetingType Type = MeetingType.Standup,
    MeetingMode? Mode = null,
    [Required, RegularExpression("^(en|da)$")] string Language = "en");

/// <param name="State">Pause, resume (<c>live</c>) or end the meeting. Ended is final.</param>
/// <param name="Mode">Switch between suggest and full auto.</param>
public sealed record UpdateMeetingRequest(MeetingState? State = null, MeetingMode? Mode = null);

/// <param name="Text">What was said, one utterance or a few sentences.</param>
public sealed record TranscriptRequest([Required, StringLength(4000, MinimumLength = 1)] string Text);

/// <param name="Pending">Proposals still waiting for a decision, including ones that need an issue picked.</param>
public sealed record MeetingCounts(int Pending, int Applied, int Rejected, int Undone);

/// <param name="CanTranscribe">A speech-to-text model is set up for the meeting's language, so browsers can send audio.</param>
public sealed record MeetingResponse(
    Guid Id,
    string Project,
    MeetingType Type,
    MeetingMode Mode,
    string Language,
    MeetingState State,
    MemberRef StartedBy,
    MemberRef Agent,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    MeetingCounts Counts,
    bool CanTranscribe);

public sealed record TranscriptSegmentResponse(Guid Id, MemberRef Speaker, string Text, DateTimeOffset At);

/// <summary>A proposed ticket action and its diff.</summary>
/// <param name="Issue">Target issue; null when the reference was ambiguous (see <c>Candidates</c>) or for a new issue.</param>
/// <param name="Candidates">Issues a vague reference matched; approving needs one picked.</param>
/// <param name="Value">Canonical new value: state name, sprint id, member id, hours, comment text or new issue title.</param>
/// <param name="ValueDisplay">The new value as people read it (sprint and member names).</param>
/// <param name="BeforeDisplay">The current value as people read it.</param>
/// <param name="Snippet">Transcript words that triggered the proposal.</param>
/// <param name="Confidence">0–1. Full auto applies only confident proposals.</param>
/// <param name="NeedsIssue">True while no single issue is chosen.</param>
public sealed record ProposalResponse(
    Guid Id,
    Guid MeetingId,
    IssueRef? Issue,
    IReadOnlyList<IssueRef> Candidates,
    ProposalOperation Operation,
    string? Value,
    string? ValueDisplay,
    string? Before,
    string? BeforeDisplay,
    string? Snippet,
    double Confidence,
    ProposalStatus Status,
    bool NeedsIssue,
    bool Edited,
    bool AutoApplied,
    MemberRef? ReviewedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AppliedAt);

/// <summary>What a transcript chunk produced.</summary>
/// <param name="Text">The text added to the transcript (the transcription, for audio).</param>
public sealed record TranscriptResult(string Text, IReadOnlyList<ProposalResponse> Proposals);

public enum ReviewDecision
{
    Approve,
    Reject,

    /// <summary>Save a changed value or issue without applying.</summary>
    Edit,
}

/// <param name="Id">Proposal id.</param>
/// <param name="Decision">Approve applies the proposal (with any edit), reject discards it.</param>
/// <param name="Value">Changed value, e.g. another state, sprint id, member id or hours.</param>
/// <param name="Issue">Issue key; picks one of the candidates or retargets the proposal.</param>
public sealed record ProposalReview(
    Guid Id,
    ReviewDecision Decision,
    [StringLength(4000)] string? Value = null,
    [StringLength(24)] string? Issue = null);

/// <param name="Items">Decisions, applied in order. To approve all of a ticket's proposals, list them all.</param>
public sealed record ReviewProposalsRequest([Required, MinLength(1), MaxLength(100)] IReadOnlyList<ProposalReview> Items);

/// <param name="Error">Why this decision failed; the proposal is unchanged.</param>
public sealed record ProposalReviewResult(ProposalResponse Proposal, IssueError? Error);

/// <summary>How suggest-mode proposals of one operation fared: the trust signal next to the full-auto toggle.</summary>
public sealed record OperationStats(ProposalOperation Operation, int Decided, int ApprovedUnchanged, int Edited, int Rejected);

/// <param name="FullAutoOperations">Operations full auto applies without approval.</param>
/// <param name="TranscriptRetentionDays">Days transcripts and snippets are kept; 0 means until the meeting ends.</param>
/// <param name="Stats">Suggest-mode history per operation.</param>
public sealed record MeetingSettingsResponse(
    MeetingMode DefaultMode,
    bool FullAutoEnabled,
    IReadOnlyList<ProposalOperation> FullAutoOperations,
    int TranscriptRetentionDays,
    IReadOnlyList<OperationStats> Stats);

/// <param name="FullAutoEnabled">Allow full auto in this workspace.</param>
/// <param name="FullAutoOperations">Operations full auto may apply. Creating issues always waits for approval.</param>
/// <param name="TranscriptRetentionDays">Days to keep transcripts and snippets; 0 deletes them when the meeting ends.</param>
public sealed record UpdateMeetingSettingsRequest(
    MeetingMode? DefaultMode = null,
    bool? FullAutoEnabled = null,
    [MaxLength(6)] IReadOnlyList<ProposalOperation>? FullAutoOperations = null,
    [Range(0, 365)] int? TranscriptRetentionDays = null);
