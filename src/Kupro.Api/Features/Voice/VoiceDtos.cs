using System.ComponentModel.DataAnnotations;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Meetings;
using Microsoft.AspNetCore.Mvc;

namespace Kupro.Api.Features.Voice;

/// <summary>What one utterance does (task 036).</summary>
public enum VoiceKind
{
    /// <summary>Proposed issue changes, in the meeting agent's operations; nothing is applied yet.</summary>
    Operations,

    /// <summary>A place in the app to open; the web router carries it out.</summary>
    Navigate,

    /// <summary>Nothing to do; <c>reason</c> says why.</summary>
    None,
}

/// <summary>Why a proposed change waits for a tap even when the person lets Kupro apply without asking.</summary>
public enum VoiceHold
{
    /// <summary>Confidence below 0.8.</summary>
    LowConfidence,

    /// <summary>The reference matched several issues, or none: pick one.</summary>
    Ambiguous,

    /// <summary>Cancels an issue.</summary>
    Destructive,

    /// <summary>Creating an issue always waits.</summary>
    CreateIssue,
}

/// <summary>Where a navigation goes. Every view is in the person's own workspace.</summary>
public enum VoiceView
{
    /// <summary>One issue (<c>issue</c>), opened over the current view.</summary>
    Issue,

    /// <summary>Issues assigned to the caller.</summary>
    MyIssues,

    /// <summary>A project's issue list (<c>project</c>), optionally filtered by <c>assignee</c>.</summary>
    List,

    /// <summary>A project's board (<c>project</c>), optionally filtered by <c>assignee</c>.</summary>
    Board,

    /// <summary>Sprints, for <c>project</c> when known; <c>sprint</c> names the one that was asked for.</summary>
    Sprints,

    /// <summary>The delivery plan, for <c>project</c> when known.</summary>
    Plan,
}

/// <summary>Why an utterance did nothing.</summary>
public enum VoiceReason
{
    /// <summary>No words were heard.</summary>
    NothingHeard,

    /// <summary>Words, but no change or place Kupro understood.</summary>
    NotUnderstood,

    /// <summary>The issue, project, person or sprint asked for isn't in this workspace.</summary>
    NotFound,

    /// <summary>A board, list or new issue needs a project, and none was said or on screen.</summary>
    ProjectNeeded,
}

/// <param name="Text">What was said or typed: one utterance.</param>
/// <param name="Issue">Key of the issue on screen: what "this one" means.</param>
/// <param name="Project">Key of the project on screen: where "next sprint", boards and new issues go when none is said.</param>
/// <param name="Language">Spoken language, <c>en</c> or <c>da</c>; default the caller's UI language.</param>
public sealed record VoiceInterpretRequest(
    [Required, StringLength(1000, MinimumLength = 1)] string Text,
    [StringLength(24)] string? Issue = null,
    [StringLength(10)] string? Project = null,
    [AllowedValues("en", "da", null)] string? Language = null);

/// <summary>The screen an utterance was spoken on, for audio requests (their body is the WAV).</summary>
public sealed class VoiceAudioQuery
{
    /// <summary>Key of the issue on screen: what "this one" means.</summary>
    [FromQuery(Name = "issue"), StringLength(24)]
    public string? Issue { get; init; }

    /// <summary>Key of the project on screen.</summary>
    [FromQuery(Name = "project"), StringLength(10)]
    public string? Project { get; init; }

    /// <summary>Spoken language, <c>en</c> or <c>da</c>; default the caller's UI language.</summary>
    [FromQuery(Name = "language"), AllowedValues("en", "da", null)]
    public string? Language { get; init; }
}

/// <summary>Dictation options; the body is the WAV.</summary>
public sealed class VoiceTranscribeQuery
{
    /// <summary>Spoken language, <c>en</c> or <c>da</c>; default the caller's UI language.</summary>
    [FromQuery(Name = "language"), AllowedValues("en", "da", null)]
    public string? Language { get; init; }

    /// <summary>Also suggest the text with spelling and punctuation fixed, and nothing else.</summary>
    [FromQuery(Name = "tidy")]
    public bool Tidy { get; init; }
}

/// <summary>
/// One proposed change, in the meeting proposal's shape. Nothing is applied: the client applies it through the issue
/// endpoints with <c>update</c>, <c>create</c> or a comment of <c>value</c>, and undoes it with <c>undo</c>.
/// </summary>
/// <param name="Issue">Target issue; null when the reference was ambiguous (see <c>Candidates</c>) or for a new issue.</param>
/// <param name="Candidates">Issues a vague reference matched; applying needs one picked.</param>
/// <param name="Value">Canonical new value: state name, sprint id, member id, hours, comment text or new issue title.</param>
/// <param name="ValueDisplay">The new value as people read it (sprint and member names).</param>
/// <param name="Before">The field's current canonical value.</param>
/// <param name="BeforeDisplay">The current value as people read it.</param>
/// <param name="Snippet">The words that triggered it.</param>
/// <param name="Confidence">0–1.</param>
/// <param name="NeedsIssue">True while no single issue is chosen.</param>
/// <param name="Held">Why it waits for a tap even with "apply without asking" on; empty means it may apply at once.</param>
/// <param name="Update">For a field change: the body for <c>PATCH issues/{key}</c>.</param>
/// <param name="Undo">For a field change: the body that puts the field back.</param>
/// <param name="Create">For a new issue: the body for <c>POST issues</c>.</param>
public sealed record VoiceOperation(
    IssueRef? Issue,
    IReadOnlyList<IssueRef> Candidates,
    ProposalOperation Operation,
    string Value,
    string? ValueDisplay,
    string? Before,
    string? BeforeDisplay,
    string? Snippet,
    double Confidence,
    bool NeedsIssue,
    IReadOnlyList<VoiceHold> Held,
    UpdateIssueRequest? Update,
    UpdateIssueRequest? Undo,
    CreateIssueRequest? Create);

/// <summary>A place to open: structured, checked against this workspace, never a URL from the model.</summary>
/// <param name="Issue">Issue key, for <c>issue</c>.</param>
/// <param name="Project">Project key.</param>
/// <param name="Assignee">Only this person's issues.</param>
/// <param name="Sprint">The sprint that was asked for ("next sprint").</param>
/// <param name="SprintName">Its name, for the confirmation line.</param>
public sealed record VoiceTarget(VoiceView View, string? Issue, string? Project, MemberRef? Assignee, Guid? Sprint, string? SprintName);

/// <summary>What one utterance means. Interpreting changes nothing.</summary>
/// <param name="Text">What was heard (the transcription, for audio).</param>
/// <param name="Operations">For <c>operations</c>: the proposed changes.</param>
/// <param name="Navigate">For <c>navigate</c>: where to go.</param>
/// <param name="Reason">For <c>none</c>: why.</param>
/// <param name="Extractor">What read it. <c>keyword</c> understands only clear commands.</param>
/// <param name="BudgetSpent">The workspace's AI hours are used up, so keyword matching was used.</param>
public sealed record VoiceInterpretation(
    VoiceKind Kind,
    string Text,
    IReadOnlyList<VoiceOperation> Operations,
    VoiceTarget? Navigate,
    VoiceReason? Reason,
    ExtractorKind Extractor,
    bool BudgetSpent);

/// <param name="Text">What was said, as transcribed: inserted at the cursor as is.</param>
/// <param name="Tidied">With <c>tidy</c>: the same words with spelling and punctuation fixed, as a suggestion; null when
/// nothing needed fixing or no model could check it.</param>
public sealed record VoiceTranscription(string Text, string? Tidied);
