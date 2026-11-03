using System.ComponentModel.DataAnnotations;
using Kupro.Api.Features.Issues;

namespace Kupro.Api.Features.Sprints;

public enum UnfinishedDestination
{
    /// <summary>The project's next planned sprint (earliest start).</summary>
    Next,

    /// <summary>Out of any sprint; state is kept.</summary>
    Backlog,
}

/// <summary>What happens to one unfinished issue when its sprint closes (ADR-020).</summary>
public enum CloseOutcome
{
    /// <summary>Moves to the target sprint.</summary>
    Carry,

    /// <summary>Done here with the hours done; a new issue takes the hours left to the target sprint.</summary>
    Split,

    /// <summary>Leaves the sprint; state is kept.</summary>
    Backlog,

    /// <summary>Canceled; stays in the closing sprint.</summary>
    Cancel,

    /// <summary>Done; stays in the closing sprint.</summary>
    Done,
}

/// <param name="Issue">Issue key.</param>
/// <param name="Outcome">What happens to it.</param>
/// <param name="HoursLeft">Split only: hours that move on, above 0 and below the issue's effort.</param>
/// <param name="Target">Carry and split only: sprint for this issue, instead of the request's target.</param>
public sealed record CloseSprintItem(
    [Required, StringLength(24)] string Issue,
    CloseOutcome Outcome,
    decimal? HoursLeft = null,
    Guid? Target = null);

/// <param name="MoveUnfinishedTo">Outcome for issues not in <c>items</c>: <c>next</c> carries them, <c>backlog</c> moves them out.</param>
/// <param name="Target">Sprint for carried and split work: an open sprint of the same project. Default: the next planned one.</param>
/// <param name="Items">Outcome per issue. Issues no longer unfinished in the sprint are skipped and reported.</param>
/// <param name="StartNext">Start the target sprint right after closing.</param>
public sealed record CloseSprintRequest(
    UnfinishedDestination MoveUnfinishedTo = UnfinishedDestination.Next,
    Guid? Target = null,
    [MaxLength(1000)] IReadOnlyList<CloseSprintItem>? Items = null,
    bool StartNext = false);

/// <param name="Outcome">The outcome.</param>
/// <param name="Count">Issues that got it.</param>
/// <param name="Hours">Their hours; for a split, the hours left.</param>
public sealed record CloseOutcomeTotal(CloseOutcome Outcome, int Count, decimal Hours);

/// <param name="Original">Key of the issue that is now Done.</param>
/// <param name="Continuation">Key of the new issue.</param>
/// <param name="HoursDone">Effort kept on the original.</param>
/// <param name="HoursLeft">Effort on the continuation.</param>
public sealed record SprintSplit(string Original, string Continuation, decimal HoursDone, decimal HoursLeft);

/// <param name="Sprint">The closed sprint.</param>
/// <param name="Moved">How many unfinished issues left the sprint (carried, split or to the backlog).</param>
/// <param name="MovedTo">Default target sprint; null when no carried or split issue went there.</param>
/// <param name="Outcomes">Count and hours per outcome that was used.</param>
/// <param name="Splits">The splits made.</param>
/// <param name="Skipped">Keys in <c>items</c> that were no longer unfinished in the sprint.</param>
/// <param name="Started">The target sprint, when <c>startNext</c> started it.</param>
public sealed record CloseSprintResponse(
    SprintResponse Sprint,
    int Moved,
    SprintResponse? MovedTo,
    IReadOnlyList<CloseOutcomeTotal> Outcomes,
    IReadOnlyList<SprintSplit> Splits,
    IReadOnlyList<string> Skipped,
    SprintResponse? Started);

/// <summary>Hours per line of the sprint report, from the ledger.</summary>
/// <param name="AtStart">Scope when the sprint started. For a sprint already active when the ledger shipped, scope at that moment.</param>
/// <param name="Added">Added during the sprint.</param>
/// <param name="Removed">Moved out during the sprint.</param>
/// <param name="Reestimated">Net effort changes during the sprint.</param>
/// <param name="Completed">Done, including the hours done on split issues.</param>
/// <param name="Carried">Carried to another sprint on close.</param>
/// <param name="Split">Hours left on split issues, moved on with their continuations.</param>
/// <param name="ToBacklog">Moved to the backlog on close.</param>
/// <param name="Canceled">Canceled.</param>
/// <param name="Remaining">Still open in the sprint; 0 once closed.</param>
/// <param name="CompletionPercent">Completed as a share of scope, before anything left on close.</param>
public sealed record SprintReviewSummary(
    decimal AtStart,
    decimal Added,
    decimal Removed,
    decimal Reestimated,
    decimal Completed,
    decimal Carried,
    decimal Split,
    decimal ToBacklog,
    decimal Canceled,
    decimal Remaining,
    int CompletionPercent);

/// <param name="HasChildren">Issues with children can't be split.</param>
/// <param name="Outcome">Set once the sprint closed, for issues it carried, split or moved to the backlog. Issues it marked done or canceled have none.</param>
/// <param name="ContinuedIn">Key of the continuation, for a split issue.</param>
public sealed record SprintReviewIssue(
    string Key,
    string Title,
    IssueType Type,
    IssueState State,
    MemberRef? Assignee,
    decimal? Effort,
    bool HasChildren,
    CloseOutcome? Outcome,
    string? ContinuedIn);

/// <param name="Sprint">The sprint.</param>
/// <param name="Summary">Hours per report line.</param>
/// <param name="Unfinished">Active sprint: open issues. Closed sprint: those that were carried or moved to the backlog.</param>
/// <param name="Completed">Done issues in the sprint; splits name their continuation.</param>
/// <param name="Targets">Open sprints of the project that unfinished work can go to.</param>
public sealed record SprintReview(
    SprintResponse Sprint,
    SprintReviewSummary Summary,
    IReadOnlyList<SprintReviewIssue> Unfinished,
    IReadOnlyList<SprintReviewIssue> Completed,
    IReadOnlyList<SprintResponse> Targets);

public enum BurndownGrouping
{
    /// <summary>Also return one series per assignee.</summary>
    Assignee,
}

/// <param name="Date">Europe/Copenhagen day; values are as of its end.</param>
/// <param name="Scope">Hours in scope; null before the ledger starts and for days still to come.</param>
/// <param name="Remaining">Hours remaining; null like <c>scope</c>.</param>
public sealed record BurndownPoint(DateOnly Date, decimal? Scope, decimal? Remaining);

/// <param name="Date">Europe/Copenhagen day.</param>
/// <param name="Hours">Where the ideal line is at the end of that day: baseline scope (see <c>atStart</c> on the review) to 0 over the sprint's weekdays.</param>
public sealed record BurndownIdeal(DateOnly Date, decimal Hours);

/// <param name="Assignee">The person; null for unassigned work.</param>
/// <param name="AtStart">Hours at start.</param>
/// <param name="Added">Hours added or reassigned to them, net.</param>
/// <param name="Completed">Hours done.</param>
/// <param name="Remaining">Hours still open in the sprint.</param>
/// <param name="Carried">Hours carried on close, including split hours left.</param>
/// <param name="Points">Their series; team series are the sum of these.</param>
public sealed record BurndownAssignee(
    MemberRef? Assignee,
    decimal AtStart,
    decimal Added,
    decimal Completed,
    decimal Remaining,
    decimal Carried,
    IReadOnlyList<BurndownPoint> Points);

/// <param name="SprintId">The sprint.</param>
/// <param name="StartDate">First day.</param>
/// <param name="EndDate">Last planned day.</param>
/// <param name="ClosedOn">Day it closed, in Europe/Copenhagen.</param>
/// <param name="Points">Team series, one per day from the first day (or the day it started, if earlier) to the later of the end date and the close.</param>
/// <param name="Ideal">Ideal line for the same days.</param>
/// <param name="Assignees">Per assignee, with <c>by=assignee</c>.</param>
public sealed record SprintBurndown(
    Guid SprintId,
    DateOnly StartDate,
    DateOnly EndDate,
    DateOnly? ClosedOn,
    IReadOnlyList<BurndownPoint> Points,
    IReadOnlyList<BurndownIdeal> Ideal,
    IReadOnlyList<BurndownAssignee>? Assignees);
