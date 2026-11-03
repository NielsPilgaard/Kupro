using System.ComponentModel.DataAnnotations;
using Kupro.Api.Data;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Meetings;
using Kupro.Api.Features.Realtime;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Kupro.Api.Features.Sprints;

/// <param name="Project">Project key.</param>
/// <param name="Name">Sprint name, e.g. "Sprint 14".</param>
/// <param name="StartDate">First day.</param>
/// <param name="EndDate">Last day.</param>
/// <param name="Goal">What the sprint should achieve.</param>
public sealed record CreateSprintRequest(
    [Required, StringLength(10)] string Project,
    [Required, StringLength(100, MinimumLength = 1)] string Name,
    DateOnly StartDate,
    DateOnly EndDate,
    [StringLength(1000)] string? Goal = null);

/// <param name="Name">New name.</param>
/// <param name="StartDate">New first day.</param>
/// <param name="EndDate">New last day.</param>
/// <param name="Goal">New goal.</param>
public sealed record UpdateSprintRequest(
    [StringLength(100, MinimumLength = 1)] string? Name = null,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    [StringLength(1000)] string? Goal = null);

/// <param name="Id">Sprint id.</param>
/// <param name="ProjectKey">Project key.</param>
/// <param name="Name">Name.</param>
/// <param name="Goal">Goal.</param>
/// <param name="StartDate">First day.</param>
/// <param name="EndDate">Last day.</param>
/// <param name="State">Planned, active or closed.</param>
/// <param name="IssueCount">Issues in the sprint.</param>
/// <param name="Effort">Total effort of its issues, in hours.</param>
/// <param name="DoneEffort">Effort of its done issues, in hours.</param>
public sealed record SprintResponse(
    Guid Id,
    string ProjectKey,
    string Name,
    string? Goal,
    DateOnly StartDate,
    DateOnly EndDate,
    SprintState State,
    int IssueCount,
    decimal Effort,
    decimal DoneEffort);

/// <summary>Sprints per project (task 010). Owns Sprint; issue membership changes go through IssueService.</summary>
public sealed class SprintService(
    IDbContextFactory<KuproDbContext> dbFactory,
    WorkspaceContext workspace,
    IssueService issues,
    SprintLedger ledger,
    RealtimeEvents realtime,
    TimeProvider clock)
{
    public async Task<IssueResult<IReadOnlyList<SprintResponse>>> ListAsync(string? projectKey, SprintState[]? states, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sprints = db.Sprints.AsNoTracking();
        if (projectKey is not null)
        {
            var key = projectKey.ToUpperInvariant();
            if (!await db.Projects.AnyAsync(p => p.Key == key, ct))
            {
                return new IssueError("project_not_found", $"No project {projectKey}.");
            }

            sprints = sprints.Where(s => db.Projects.Any(p => p.Id == s.ProjectId && p.Key == key));
        }

        if (states is { Length: > 0 })
        {
            sprints = sprints.Where(s => states.Contains(s.State));
        }

        return await Project(db, sprints.OrderBy(s => s.StartDate)).ToListAsync(ct);
    }

    public async Task<IssueResult<SprintResponse>> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Project(db, db.Sprints.AsNoTracking().Where(s => s.Id == id)).SingleOrDefaultAsync(ct) is { } sprint
            ? sprint
            : NotFound(id);
    }

    public async Task<IssueResult<SprintResponse>> CreateAsync(CreateSprintRequest request, CancellationToken ct)
    {
        if (DatesError(request.StartDate, request.EndDate) is { } datesError)
        {
            return datesError;
        }

        var key = request.Project.ToUpperInvariant();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var projectId = await db.Projects.Where(p => p.Key == key).Select(p => (Guid?)p.Id).SingleOrDefaultAsync(ct);
        if (projectId is null)
        {
            return new IssueError("project_not_found", $"No project {request.Project}.");
        }

        var sprint = new Sprint
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.RequireWorkspaceId(),
            ProjectId = projectId.Value,
            Name = request.Name.Trim(),
            Goal = request.Goal,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            State = SprintState.Planned,
            CreatedAt = clock.GetUtcNow(),
        };
        db.Sprints.Add(sprint);
        await db.SaveChangesAsync(ct);
        await realtime.ChangedAsync(ChangeTopic.Sprints);
        return await GetAsync(sprint.Id, ct);
    }

    public async Task<IssueResult<SprintResponse>> UpdateAsync(Guid id, UpdateSprintRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sprint = await db.Sprints.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (sprint is null)
        {
            return NotFound(id);
        }

        sprint.Name = request.Name?.Trim() ?? sprint.Name;
        sprint.Goal = request.Goal ?? sprint.Goal;
        sprint.StartDate = request.StartDate ?? sprint.StartDate;
        sprint.EndDate = request.EndDate ?? sprint.EndDate;
        if (DatesError(sprint.StartDate, sprint.EndDate) is { } datesError)
        {
            return datesError;
        }

        await db.SaveChangesAsync(ct);
        await realtime.ChangedAsync(ChangeTopic.Sprints);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// Starts a planned sprint. Fails while the project has another active sprint. The row lock orders it against issue
    /// writes, so an issue joining at this moment lands in the baseline or gets its own ledger row.
    /// </summary>
    public async Task<IssueResult<SprintResponse>> StartAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        IssueError? error = null;
        await db.Database.CreateExecutionStrategy().ExecuteAsync<object?, bool>(null, async (_, _, _) =>
        {
            db.ChangeTracker.Clear();
            error = null;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var sprint = await db.Sprints.FromSql($"SELECT * FROM sprints WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (sprint is null)
            {
                error = NotFound(id);
                return false;
            }

            if (sprint.State != SprintState.Planned)
            {
                error = new IssueError("sprint_not_planned", $"{sprint.Name} is {sprint.State}; only planned sprints start.");
                return false;
            }

            sprint.State = SprintState.Active;
            await ledger.RecordBaselineAsync(db, sprint.Id, ct);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                error = new IssueError("sprint_already_active", "The project already has an active sprint. Close it first.");
                return false;
            }

            await tx.CommitAsync(ct);
            return true;
        }, async (_, _, _) => new ExecutionResult<bool>(await db.Sprints.AnyAsync(s => s.Id == id && s.State == SprintState.Active, ct), true), ct);

        if (error is not null)
        {
            return error;
        }

        await realtime.ChangedAsync(ChangeTopic.Sprints);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// Closes the active sprint and applies an outcome to each unfinished issue (ADR-020), all in one transaction. The row
    /// lock makes a second close wait, then find the sprint closed.
    /// </summary>
    public async Task<IssueResult<CloseSprintResponse>> CloseAsync(Guid id, CloseSprintRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        AppliedClose? applied = null;
        IssueError? error = null;
        Guid? defaultTarget = null;
        Guid? started = null;

        await db.Database.CreateExecutionStrategy().ExecuteAsync<object?, bool>(null, async (_, _, _) =>
        {
            db.ChangeTracker.Clear();
            applied = null;
            error = null;
            defaultTarget = null;
            started = null;
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var sprint = await db.Sprints.FromSql($"SELECT * FROM sprints WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (sprint is null)
            {
                error = NotFound(id);
                return false;
            }

            if (sprint.State != SprintState.Active)
            {
                error = new IssueError("sprint_not_active", $"{sprint.Name} isn't active.");
                return false;
            }

            var open = await db.Sprints.AsNoTracking()
                .Where(s => s.ProjectId == sprint.ProjectId && s.State == SprintState.Planned)
                .OrderBy(s => s.StartDate).Select(s => s.Id).ToListAsync(ct);
            if (request.Target is { } wanted && !open.Contains(wanted))
            {
                error = new IssueError("invalid_sprint", $"Sprint {wanted} isn't an open sprint in this project, other than the one closing.");
                return false;
            }

            Guid? target = request.Target ?? (open.Count > 0 ? open[0] : null);
            if (request.StartNext && target is null)
            {
                error = new IssueError("no_next_sprint", "There is no planned sprint to start. Create one first.");
                return false;
            }

            var result = await issues.ApplyCloseAsync(db, sprint, request, target, open, ct);
            if (result.Error is not null)
            {
                error = result.Error;
                return false;
            }

            sprint.State = SprintState.Closed;
            sprint.ClosedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);

            if (request.StartNext)
            {
                // After the close is saved: the database allows one active sprint per project.
                var next = await db.Sprints.SingleAsync(s => s.Id == target, ct);
                next.State = SprintState.Active;
                await ledger.RecordBaselineAsync(db, next.Id, ct);
                await db.SaveChangesAsync(ct);
                started = next.Id;
            }

            applied = result.Value;
            defaultTarget = target;
            await tx.CommitAsync(ct);
            return true;
        }, async (_, _, _) => new ExecutionResult<bool>(await db.Sprints.AnyAsync(s => s.Id == id && s.State == SprintState.Closed, ct), true), ct);

        if (error is not null)
        {
            return error;
        }

        if (applied is null)
        {
            return new IssueError("sprint_not_active", "The sprint isn't active.");
        }

        await issues.PublishCloseAsync(applied, ct);
        await realtime.ChangedAsync(ChangeTopic.Sprints);

        var closed = (await GetAsync(id, ct)).Value!;
        var movedTo = applied.UsedDefaultTarget && defaultTarget is { } t ? (await GetAsync(t, ct)).Value : null;
        var startedSprint = started is { } s ? (await GetAsync(s, ct)).Value : null;
        return new CloseSprintResponse(closed, applied.Moved, movedTo, applied.Outcomes, applied.Splits, applied.Skipped, startedSprint);
    }

    /// <summary>The close review while the sprint is active, the read-only sprint report once it closed.</summary>
    public async Task<IssueResult<SprintReview>> ReviewAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sprint = await Project(db, db.Sprints.AsNoTracking().Where(s => s.Id == id)).SingleOrDefaultAsync(ct);
        if (sprint is null)
        {
            return NotFound(id);
        }

        var rows = await LedgerTotalsAsync(db, id, ct);
        var outcomes = await db.SprintLedger.AsNoTracking()
            .Where(l => l.SprintId == id && (l.Kind == LedgerKind.Carried || l.Kind == LedgerKind.Split || l.Kind == LedgerKind.Backlogged))
            .Select(l => new { l.IssueId, l.Kind }).ToListAsync(ct);
        var outcomeOf = outcomes.ToDictionary(o => o.IssueId, o => o.Kind switch
        {
            LedgerKind.Carried => CloseOutcome.Carry,
            LedgerKind.Split => CloseOutcome.Split,
            _ => CloseOutcome.Backlog,
        });
        var leftIds = outcomes.Where(o => o.Kind != LedgerKind.Split).Select(o => o.IssueId).ToList();

        var issueRows = await db.Issues.AsNoTracking()
            .Where(i => i.SprintId == id || leftIds.Contains(i.Id))
            .OrderBy(i => i.Rank)
            .Select(i => new
            {
                i.Id,
                i.Key,
                i.Title,
                i.Type,
                i.State,
                Assignee = db.Members.Where(m => m.Id == i.AssigneeId).Select(MemberRefs.Projection(db)).FirstOrDefault(),
                i.Effort,
                HasChildren = db.Issues.Any(c => c.ParentId == i.Id),
                SplitData = db.Activity.Where(a => a.IssueId == i.Id && a.Kind == IssueService.SplitIntoKind)
                    .OrderByDescending(a => a.CreatedAt).Select(a => a.Data).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var unfinished = new List<SprintReviewIssue>();
        var completed = new List<SprintReviewIssue>();
        foreach (var i in issueRows)
        {
            var row = new SprintReviewIssue(i.Key, i.Title, i.Type, i.State, i.Assignee, i.Effort, i.HasChildren,
                outcomeOf.TryGetValue(i.Id, out var outcome) ? outcome : null, IssueService.SplitKey(i.SplitData));
            // Reopened after the close: not what the sprint delivered, and the report's numbers are from the ledger anyway.
            var stillOpen = sprint.State != SprintState.Closed && i.State != IssueState.Done && i.State != IssueState.Canceled;
            if (leftIds.Contains(i.Id) || stillOpen)
            {
                unfinished.Add(row);
            }
            else if (i.State == IssueState.Done)
            {
                completed.Add(row);
            }
        }

        var targets = sprint.State == SprintState.Closed ? [] : await Project(db, db.Sprints.AsNoTracking()
            .Where(s => s.Id != id && s.State == SprintState.Planned && db.Projects.Any(p => p.Id == s.ProjectId && p.Key == sprint.ProjectKey))
            .OrderBy(s => s.StartDate)).ToListAsync(ct);
        return new SprintReview(sprint, Summarize(rows), unfinished, completed, targets);
    }

    /// <summary>Team burndown per Europe/Copenhagen day, and per assignee when asked for (ADR-020).</summary>
    public async Task<IssueResult<SprintBurndown>> BurndownAsync(Guid id, BurndownGrouping? by, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sprint = await db.Sprints.AsNoTracking().Where(s => s.Id == id)
            .Select(s => new { s.StartDate, s.EndDate, s.ClosedAt }).SingleOrDefaultAsync(ct);
        if (sprint is null)
        {
            return NotFound(id);
        }

        var rows = await LedgerTotalsAsync(db, id, ct);
        var lines = rows.Where(r => !IsLeaving(r.Kind)).ToList();
        DateOnly? closedOn = sprint.ClosedAt is { } closedAt ? LocalDay(closedAt) : null;
        var lastDay = closedOn is { } c && c > sprint.EndDate ? c : sprint.EndDate;
        var firstKnown = lines.Count > 0 ? lines.Min(r => r.Day) : (DateOnly?)null;
        // Started ahead of its first planned day: the chart starts when the work did.
        var firstDay = firstKnown is { } known && known < sprint.StartDate ? known : sprint.StartDate;
        var days = Enumerable.Range(0, Math.Max(lastDay.DayNumber - firstDay.DayNumber, 0) + 1)
            .Select(firstDay.AddDays).ToList();
        var lastKnown = closedOn ?? LocalDay(clock.GetUtcNow());

        List<BurndownPoint> Series(IEnumerable<LedgerTotal> source)
        {
            var sorted = source.OrderBy(r => r.Day).ToList();
            var next = 0;
            decimal scope = 0, remaining = 0;
            var points = new List<BurndownPoint>(days.Count);
            foreach (var day in days)
            {
                for (; next < sorted.Count && sorted[next].Day <= day; next++)
                {
                    scope += sorted[next].Scope;
                    remaining += sorted[next].Remaining;
                }

                points.Add(firstKnown is null || day < firstKnown || day > lastKnown
                    ? new BurndownPoint(day, null, null)
                    : new BurndownPoint(day, scope, remaining));
            }

            return points;
        }

        IReadOnlyList<BurndownAssignee>? assignees = null;
        if (by == BurndownGrouping.Assignee)
        {
            var refs = await MemberRefs.ByIdAsync(db, rows.Select(r => r.AssigneeId).OfType<Guid>(), ct);
            assignees = rows.GroupBy(r => r.AssigneeId)
                .Select(g =>
                {
                    var own = g.ToList();
                    return new BurndownAssignee(
                        g.Key is { } member ? refs.GetValueOrDefault(member) : null,
                        own.Where(r => r.Kind == LedgerKind.Baseline).Sum(r => r.Scope),
                        own.Where(r => r.Kind is LedgerKind.Added or LedgerKind.Reassigned).Sum(r => r.Scope),
                        own.Sum(r => r.Scope - r.Remaining),
                        own.Sum(r => r.Remaining),
                        -own.Where(r => r.Kind is LedgerKind.Carried or LedgerKind.Split).Sum(r => r.Scope),
                        Series(own.Where(r => !IsLeaving(r.Kind)).ToList()));
                })
                .OrderBy(a => a.Assignee is null).ThenBy(a => a.Assignee?.Name, StringComparer.CurrentCulture)
                .ToList();
        }

        var baseline = rows.Where(r => r.Kind == LedgerKind.Baseline).Sum(r => r.Scope);
        return new SprintBurndown(id, sprint.StartDate, sprint.EndDate, closedOn, Series(lines),
            IdealLine(baseline, sprint.EndDate, days), assignees);
    }

    /// <summary>Baseline scope down to 0 by the end of the last day, falling on weekdays only. No holidays (ADR-020).</summary>
    private static List<BurndownIdeal> IdealLine(decimal baseline, DateOnly end, List<DateOnly> days)
    {
        var weekdays = days.Count(d => d <= end && !IsWeekend(d));
        var elapsed = 0;
        return days.Select(day =>
        {
            elapsed += day <= end && !IsWeekend(day) ? 1 : 0;
            var hours = weekdays == 0
                ? (day >= end ? 0 : baseline)
                : decimal.Round(baseline * (weekdays - elapsed) / weekdays, 2);
            return new BurndownIdeal(day, hours);
        }).ToList();
    }

    /// <summary>Long enough for any real sprint; the burndown has a point per day, so the span has to be bounded.</summary>
    private const int MaxSprintDays = 366;

    private static IssueError? DatesError(DateOnly start, DateOnly end) =>
        end < start ? new IssueError("invalid_dates", "The sprint ends before it starts.")
        : end.DayNumber - start.DayNumber >= MaxSprintDays ? new IssueError("invalid_dates", $"A sprint can't be longer than {MaxSprintDays} days.")
        : null;

    private static bool IsWeekend(DateOnly day) => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    private static SprintReviewSummary Summarize(List<LedgerTotal> rows)
    {
        decimal Scope(params LedgerKind[] kinds) => rows.Where(r => kinds.Contains(r.Kind)).Sum(r => r.Scope);
        // Scope minus remaining is the hours done at any moment, whichever rows got it there.
        var completed = rows.Sum(r => r.Scope - r.Remaining);
        var scope = rows.Where(r => !IsLeaving(r.Kind)).Sum(r => r.Scope);
        return new SprintReviewSummary(
            AtStart: Scope(LedgerKind.Baseline),
            Added: Scope(LedgerKind.Added),
            Removed: -Scope(LedgerKind.Removed),
            Reestimated: Scope(LedgerKind.Estimated),
            Completed: completed,
            Carried: -Scope(LedgerKind.Carried),
            Split: -Scope(LedgerKind.Split),
            ToBacklog: -Scope(LedgerKind.Backlogged),
            // Completed and Reopened rows change scope only when the issue was canceled before.
            Canceled: -Scope(LedgerKind.Canceled, LedgerKind.Reopened, LedgerKind.Completed),
            Remaining: rows.Sum(r => r.Remaining),
            CompletionPercent: scope > 0 ? (int)Math.Round(completed / scope * 100, MidpointRounding.AwayFromZero) : 0);
    }

    /// <summary>Close kinds would drop remaining to zero on the chart, so they feed the summary only (ADR-020).</summary>
    private static bool IsLeaving(LedgerKind kind) => kind is LedgerKind.Carried or LedgerKind.Split or LedgerKind.Backlogged;

    /// <summary>The sprint's ledger as one grouped query; the report and burndown are sums over these rows.</summary>
    private static Task<List<LedgerTotal>> LedgerTotalsAsync(KuproDbContext db, Guid sprintId, CancellationToken ct) =>
        db.SprintLedger.AsNoTracking().Where(l => l.SprintId == sprintId)
            .GroupBy(l => new { Day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(l.At.UtcDateTime, CopenhagenId)), l.AssigneeId, l.Kind })
            .Select(g => new LedgerTotal(g.Key.Day, g.Key.AssigneeId, g.Key.Kind, g.Sum(l => l.ScopeDelta), g.Sum(l => l.RemainingDelta)))
            .ToListAsync(ct);

    private const string CopenhagenId = "Europe/Copenhagen";
    private static readonly TimeZoneInfo Copenhagen = TimeZoneInfo.FindSystemTimeZoneById(CopenhagenId);

    private static DateOnly LocalDay(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Copenhagen).DateTime);

    private sealed record LedgerTotal(DateOnly Day, Guid? AssigneeId, LedgerKind Kind, decimal Scope, decimal Remaining);

    private static IQueryable<SprintResponse> Project(KuproDbContext db, IQueryable<Sprint> sprints) =>
        sprints.Select(s => new SprintResponse(
            s.Id,
            db.Projects.Where(p => p.Id == s.ProjectId).Select(p => p.Key).First(),
            s.Name,
            s.Goal,
            s.StartDate,
            s.EndDate,
            s.State,
            db.Issues.Count(i => i.SprintId == s.Id),
            db.Issues.Where(i => i.SprintId == s.Id).Sum(i => i.Effort) ?? 0,
            db.Issues.Where(i => i.SprintId == s.Id && i.State == IssueState.Done).Sum(i => i.Effort) ?? 0));

    private static IssueError NotFound(Guid id) => new("sprint_not_found", $"No sprint {id}.");
}
