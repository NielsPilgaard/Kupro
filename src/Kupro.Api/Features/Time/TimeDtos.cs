using System.ComponentModel.DataAnnotations;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Issues;

namespace Kupro.Api.Features.Time;

/// <param name="Issue">Issue key the time was spent on.</param>
/// <param name="Minutes">Time spent, 1-1440 minutes.</param>
/// <param name="Date">Day it was spent, YYYY-MM-DD; today (Europe/Copenhagen) when omitted. Not later than today.</param>
/// <param name="Note">What it went on, plain text.</param>
/// <param name="State">
/// <c>done</c> or <c>canceled</c>: also close the issue in the same save. Both commit or neither does, and the entry
/// locks with the issue afterwards.
/// </param>
public sealed record LogTimeRequest(
    [Required, StringLength(40, MinimumLength = 1)] string Issue,
    [Range(1, TimeLimits.MinutesPerDay)] int Minutes,
    DateOnly? Date = null,
    [StringLength(TimeLimits.MaxNote)] string? Note = null,
    IssueState? State = null);

/// <param name="Minutes">New duration, 1-1440 minutes.</param>
/// <param name="Date">Move the entry to another day.</param>
/// <param name="Note">New note; an empty string clears it.</param>
public sealed record UpdateTimeEntryRequest(
    [Range(1, TimeLimits.MinutesPerDay)] int? Minutes = null,
    DateOnly? Date = null,
    [StringLength(TimeLimits.MaxNote)] string? Note = null);

/// <param name="Id">Entry id, used to edit or delete it.</param>
/// <param name="Member">Whose time it is.</param>
/// <param name="Issue">The issue it was spent on.</param>
/// <param name="ProjectKey">The issue's project.</param>
/// <param name="Date">Day it was spent.</param>
/// <param name="Minutes">Duration in minutes.</param>
/// <param name="Note">What it went on.</param>
/// <param name="Locked">The issue is Done or Canceled: the entry can't be changed until it is reopened.</param>
/// <param name="Via">Surface it was logged from.</param>
public sealed record TimeEntryResponse(
    Guid Id,
    MemberRef Member,
    IssueRef Issue,
    string ProjectKey,
    DateOnly Date,
    int Minutes,
    string? Note,
    bool Locked,
    Surface Via,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <param name="Date">The day.</param>
/// <param name="Minutes">Sum of the day's entries: the day's working time.</param>
public sealed record TimeDay(DateOnly Date, int Minutes);

/// <param name="From">First day, inclusive.</param>
/// <param name="To">Last day, inclusive.</param>
/// <param name="Entries">Your entries in the range, by day, oldest first.</param>
/// <param name="Days">Every day in the range with its total, zero days included.</param>
/// <param name="TotalMinutes">Sum over the range.</param>
public sealed record MyTime(DateOnly From, DateOnly To, IReadOnlyList<TimeEntryResponse> Entries, IReadOnlyList<TimeDay> Days, int TotalMinutes);

/// <param name="Member">The person.</param>
/// <param name="Minutes">Their time on the issue.</param>
public sealed record MemberTime(MemberRef Member, int Minutes);

/// <param name="IssueKey">The issue.</param>
/// <param name="Locked">Done or Canceled: no time can be added, edited or deleted until it is reopened.</param>
/// <param name="MyMinutes">Your time on the issue.</param>
/// <param name="TotalMinutes">Everyone's time on the issue. Owners only; null for others.</param>
/// <param name="People">Each person's share, most first. Owners only; null for others.</param>
public sealed record IssueTime(string IssueKey, bool Locked, int MyMinutes, int? TotalMinutes, IReadOnlyList<MemberTime>? People);

/// <param name="Items">Entries, by day then person.</param>
/// <param name="TotalMinutes">Sum of the entries.</param>
public sealed record TimeEntryList(IReadOnlyList<TimeEntryResponse> Items, int TotalMinutes);

/// <param name="Id">Change id; pass as <c>before</c> for older changes.</param>
/// <param name="EntryId">The entry changed. A deleted entry keeps its history.</param>
/// <param name="Member">Whose entry it is.</param>
/// <param name="ChangedBy">Who changed it: the person, or an agent acting for them.</param>
/// <param name="Issue">Issue key.</param>
/// <param name="Kind">created, updated or deleted.</param>
/// <param name="At">When.</param>
public sealed record TimeEntryChangeResponse(
    Guid Id,
    Guid EntryId,
    MemberRef Member,
    MemberRef ChangedBy,
    string Issue,
    TimeChangeKind Kind,
    Surface Via,
    DateTimeOffset At,
    DateOnly? OldDate,
    DateOnly? NewDate,
    int? OldMinutes,
    int? NewMinutes,
    string? OldNote,
    string? NewNote);

/// <summary>One row of an export: what the employer keeps (ADR-021).</summary>
/// <param name="Date">Day it was spent.</param>
/// <param name="MemberId">The person's member id.</param>
/// <param name="Member">The person's name.</param>
/// <param name="Email">The person's email, when known.</param>
/// <param name="Issue">Issue key.</param>
/// <param name="IssueTitle">Issue title.</param>
/// <param name="Minutes">Duration in minutes.</param>
/// <param name="Note">What it went on.</param>
/// <param name="Deactivated">The person has left the workspace; their entries are kept.</param>
public sealed record TimeExportRow(
    DateOnly Date,
    Guid MemberId,
    string Member,
    string? Email,
    string Issue,
    string IssueTitle,
    int Minutes,
    string? Note,
    bool Deactivated);

public enum TimeExportFormat
{
    Csv,
    Json,
}
