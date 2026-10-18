using System.ComponentModel.DataAnnotations;
using Kupro.Api.Data;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Realtime;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Api.Features.Plan;

/// <param name="FromMonth">First day of the month the headcount applies from.</param>
/// <param name="People">People working on the project from that month.</param>
public sealed record CapacityEntry(DateOnly FromMonth, [Range(0, 1000)] decimal People);

/// <param name="Entries">The full step function; replaces what was there. Empty clears it.</param>
public sealed record SetCapacityRequest([Required, MaxLength(120)] IReadOnlyList<CapacityEntry> Entries);

/// <param name="Name">Milestone name, e.g. "Launch".</param>
/// <param name="Date">The date it marks.</param>
/// <param name="Project">Project key; workspace-wide when omitted.</param>
public sealed record CreateMilestoneRequest(
    [Required, StringLength(100, MinimumLength = 1)] string Name, DateOnly Date, [StringLength(10)] string? Project = null);

/// <param name="Name">New name.</param>
/// <param name="Date">New date.</param>
public sealed record UpdateMilestoneRequest([StringLength(100, MinimumLength = 1)] string? Name = null, DateOnly? Date = null);

/// <param name="ProjectKey">Project key, or null for a workspace-wide milestone.</param>
public sealed record MilestoneResponse(Guid Id, string Name, DateOnly Date, string? ProjectKey);

/// <param name="Effort">The card's own estimate in hours.</param>
/// <param name="CountedEffort">Hours this card adds to demand: an epic's estimate only counts when none of its features
/// is estimated; a feature without its own estimate counts its tasks.</param>
/// <param name="Done">Done children.</param>
/// <param name="Total">All children.</param>
public sealed record PlanCard(
    string Key,
    string Title,
    IssueType Type,
    IssueState State,
    string ProjectKey,
    string? ParentKey,
    DateOnly? StartDate,
    DateOnly? DueDate,
    decimal? Effort,
    decimal CountedEffort,
    int Done,
    int Total);

/// <param name="Month">First day of the month.</param>
/// <param name="DemandHours">Effort scheduled in the month (each card spread evenly over its days).</param>
/// <param name="SupplyHours">People × hours per week × weeks in the month.</param>
/// <param name="DemandPeople">Demand expressed as people for the month.</param>
/// <param name="SupplyPeople">People available.</param>
public sealed record PlanMonth(DateOnly Month, decimal DemandHours, decimal SupplyHours, decimal DemandPeople, decimal SupplyPeople)
{
    /// <summary>People short (positive) or spare (negative).</summary>
    public decimal GapPeople => DemandPeople - SupplyPeople;
}

/// <param name="NoDatedWork">No epic or feature with dates in the horizon.</param>
/// <param name="CardsWithoutEffort">Epics/features with no hours counted.</param>
/// <param name="CardsWithoutDates">Epics/features missing a start or due date.</param>
public sealed record PlanHealth(bool NoDatedWork, int CardsWithoutEffort, int CardsWithoutDates);

public sealed record PlanProject(
    string Key,
    string Name,
    ProjectColor Color,
    IReadOnlyList<CapacityEntry> Capacity,
    IReadOnlyList<PlanMonth> Months,
    IReadOnlyList<PlanCard> Cards,
    PlanHealth Health);

/// <param name="Blocker">Issue that blocks.</param>
/// <param name="Blocked">Issue that waits.</param>
/// <param name="Violated">The blocker is due after the blocked card starts.</param>
public sealed record PlanDependency(string Blocker, string Blocked, bool Violated);

/// <param name="From">First month in the horizon.</param>
/// <param name="To">Last month in the horizon.</param>
/// <param name="HoursPerWeek">Workspace hours per person per week used for supply.</param>
/// <param name="Totals">Per month across the listed projects.</param>
public sealed record PlanResponse(
    DateOnly From,
    DateOnly To,
    decimal HoursPerWeek,
    IReadOnlyList<PlanProject> Projects,
    IReadOnlyList<PlanMonth> Totals,
    IReadOnlyList<MilestoneResponse> Milestones,
    IReadOnlyList<PlanDependency> Dependencies);

/// <summary>
/// The delivery plan (task 011, docs/delivery-plan.md): a read model over issues plus milestones and project capacity,
/// which this service owns. Demand is spread evenly over each card's days (OQ-3).
/// </summary>
public sealed class PlanService(
    IDbContextFactory<KuproDbContext> dbFactory, WorkspaceContext workspace, RealtimeEvents realtime, TimeProvider clock)
{
    public async Task<IssueResult<PlanResponse>> GetPlanAsync(DateOnly? from, DateOnly? to, string[]? projectKeys, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var start = MonthOf(from ?? today);
        var end = MonthOf(to ?? new DateOnly(today.Year + 1, 12, 1));
        if (end < start || (end.Year - start.Year) * 12 + end.Month - start.Month > 60)
        {
            return new IssueError("invalid_horizon", "The horizon must run forward and span at most 5 years.");
        }

        var months = Months(start, end).ToList();
        var horizonEnd = end.AddMonths(1).AddDays(-1);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var hoursPerWeek = await db.Workspaces.Where(w => w.Id == workspace.RequireWorkspaceId()).Select(w => w.HoursPerWeek).SingleAsync(ct);

        var projectQuery = db.Projects.AsNoTracking();
        if (projectKeys is { Length: > 0 })
        {
            var keys = projectKeys.Select(k => k.ToUpperInvariant()).ToList();
            projectQuery = projectQuery.Where(p => keys.Contains(p.Key));
        }

        var projects = await projectQuery.OrderBy(p => p.Name).Select(p => new { p.Id, p.Key, p.Name, p.Color }).ToListAsync(ct);
        var projectIds = projects.Select(p => p.Id).ToList();

        var cards = await db.Issues.AsNoTracking()
            .Where(i => projectIds.Contains(i.ProjectId) && (i.Type == IssueType.Epic || i.Type == IssueType.Feature))
            .Select(i => new { i.Id, i.Key, i.Title, i.Type, i.State, i.ProjectId, i.ParentId, i.StartDate, i.DueDate, i.Effort })
            .ToListAsync(ct);
        var cardIds = cards.Select(c => c.Id).ToList();

        // One grouped query for children: progress plus the task effort that backs un-estimated cards.
        var children = await db.Issues.AsNoTracking()
            .Where(i => i.ParentId != null && cardIds.Contains(i.ParentId.Value))
            .GroupBy(i => i.ParentId!.Value)
            .Select(g => new
            {
                ParentId = g.Key,
                Total = g.Count(),
                Done = g.Count(i => i.State == IssueState.Done),
                TaskEffort = g.Where(i => i.Type == IssueType.Task).Sum(i => i.Effort) ?? 0,
            })
            .ToDictionaryAsync(c => c.ParentId, ct);

        var capacity = await db.ProjectCapacity.AsNoTracking().Where(c => projectIds.Contains(c.ProjectId))
            .OrderBy(c => c.FromMonth).Select(c => new { c.ProjectId, c.FromMonth, c.People }).ToListAsync(ct);

        var milestones = await db.Milestones.AsNoTracking()
            .Where(m => m.Date >= start && m.Date <= horizonEnd && (m.ProjectId == null || projectIds.Contains(m.ProjectId.Value)))
            .OrderBy(m => m.Date)
            .Select(m => new MilestoneResponse(m.Id, m.Name, m.Date, db.Projects.Where(p => p.Id == m.ProjectId).Select(p => p.Key).FirstOrDefault()))
            .ToListAsync(ct);

        var dependencies = await db.IssueRelations.AsNoTracking()
            .Where(r => r.Type == RelationType.Blocks && cardIds.Contains(r.SourceId) && cardIds.Contains(r.TargetId))
            .Select(r => new { r.SourceId, r.TargetId })
            .ToListAsync(ct);

        var keyOf = cards.ToDictionary(c => c.Id, c => c.Key);
        var projectKeyOf = projects.ToDictionary(p => p.Id, p => p.Key);
        decimal? FeatureEffort(Guid id, decimal? own) =>
            own ?? (children.TryGetValue(id, out var ch) && ch.TaskEffort > 0 ? ch.TaskEffort : null);
        var featureEffortByEpic = cards.Where(c => c.Type == IssueType.Feature && c.ParentId is not null)
            .GroupBy(c => c.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Any(f => FeatureEffort(f.Id, f.Effort) > 0));

        // Epics whose features carry the estimate: not "without effort" even though they count 0 themselves.
        var coveredEpics = cards.Where(c => c.Type == IssueType.Epic && featureEffortByEpic.GetValueOrDefault(c.Id))
            .Select(c => c.Key).ToHashSet();

        var planCards = cards.Select(c =>
        {
            var counted = c.Type == IssueType.Feature
                ? FeatureEffort(c.Id, c.Effort) ?? 0
                : featureEffortByEpic.GetValueOrDefault(c.Id) ? 0 : FeatureEffort(c.Id, c.Effort) ?? 0;
            children.TryGetValue(c.Id, out var ch);
            return new PlanCard(c.Key, c.Title, c.Type, c.State, projectKeyOf[c.ProjectId],
                c.ParentId is { } p && keyOf.TryGetValue(p, out var parentKey) ? parentKey : null,
                c.StartDate, c.DueDate, c.Effort, counted, ch?.Done ?? 0, ch?.Total ?? 0);
        }).ToList();

        var planProjects = projects.Select(p =>
        {
            var projectCards = planCards.Where(c => c.ProjectKey == p.Key).OrderBy(c => c.StartDate ?? DateOnly.MaxValue).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
            var steps = capacity.Where(c => c.ProjectId == p.Id).Select(c => new CapacityEntry(c.FromMonth, c.People)).ToList();
            var projectMonths = months.Select(m => Month(m, projectCards, steps, hoursPerWeek)).ToList();
            var health = new PlanHealth(
                NoDatedWork: !projectCards.Any(c => c.StartDate <= horizonEnd && c.DueDate >= start),
                CardsWithoutEffort: projectCards.Count(c => c.CountedEffort == 0 && !coveredEpics.Contains(c.Key)),
                CardsWithoutDates: projectCards.Count(c => c.StartDate is null || c.DueDate is null));
            return new PlanProject(p.Key, p.Name, p.Color, steps, projectMonths, projectCards, health);
        }).ToList();

        var totals = months.Select((m, i) =>
        {
            var demand = planProjects.Sum(p => p.Months[i].DemandHours);
            var supply = planProjects.Sum(p => p.Months[i].SupplyHours);
            var perPerson = HoursPerPerson(m, hoursPerWeek);
            return new PlanMonth(m, demand, supply, Round(demand / perPerson), Round(supply / perPerson));
        }).ToList();

        var cardByKey = planCards.ToDictionary(c => c.Key);
        var deps = dependencies.Select(d =>
        {
            var blocker = cardByKey[keyOf[d.SourceId]];
            var blocked = cardByKey[keyOf[d.TargetId]];
            return new PlanDependency(blocker.Key, blocked.Key,
                blocker.DueDate is { } due && blocked.StartDate is { } st && due > st);
        }).ToList();

        return new PlanResponse(start, end, hoursPerWeek, planProjects, totals, milestones, deps);
    }

    public async Task<IssueResult<IReadOnlyList<CapacityEntry>>> GetCapacityAsync(string projectKey, CancellationToken ct)
    {
        var key = projectKey.ToUpperInvariant();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var projectId = await db.Projects.Where(p => p.Key == key).Select(p => (Guid?)p.Id).SingleOrDefaultAsync(ct);
        if (projectId is null)
        {
            return new IssueError("project_not_found", $"No project {projectKey}.");
        }

        return await db.ProjectCapacity.AsNoTracking().Where(c => c.ProjectId == projectId).OrderBy(c => c.FromMonth)
            .Select(c => new CapacityEntry(c.FromMonth, c.People)).ToListAsync(ct);
    }

    /// <summary>Replaces a project's headcount steps, e.g. "3 from Jan 2027, 5 from Jul 2027".</summary>
    public async Task<IssueResult<IReadOnlyList<CapacityEntry>>> SetCapacityAsync(string projectKey, SetCapacityRequest request, CancellationToken ct)
    {
        var key = projectKey.ToUpperInvariant();
        var entries = request.Entries.Select(e => e with { FromMonth = MonthOf(e.FromMonth) }).OrderBy(e => e.FromMonth).ToList();
        if (entries.Select(e => e.FromMonth).Distinct().Count() != entries.Count)
        {
            return new IssueError("invalid_capacity", "Each month can appear once.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var projectId = await db.Projects.Where(p => p.Key == key).Select(p => (Guid?)p.Id).SingleOrDefaultAsync(ct);
        if (projectId is null)
        {
            return new IssueError("project_not_found", $"No project {projectKey}.");
        }

        db.ProjectCapacity.RemoveRange(await db.ProjectCapacity.Where(c => c.ProjectId == projectId).ToListAsync(ct));
        db.ProjectCapacity.AddRange(entries.Select(e => new ProjectCapacity
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.RequireWorkspaceId(),
            ProjectId = projectId.Value,
            FromMonth = e.FromMonth,
            People = e.People,
        }));
        await db.SaveChangesAsync(ct);
        await realtime.ChangedAsync(ChangeTopic.Plan);
        return entries;
    }

    public async Task<IReadOnlyList<MilestoneResponse>> ListMilestonesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Milestones.AsNoTracking().OrderBy(m => m.Date)
            .Select(m => new MilestoneResponse(m.Id, m.Name, m.Date, db.Projects.Where(p => p.Id == m.ProjectId).Select(p => p.Key).FirstOrDefault()))
            .ToListAsync(ct);
    }

    public async Task<IssueResult<MilestoneResponse>> CreateMilestoneAsync(CreateMilestoneRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        Guid? projectId = null;
        if (request.Project is { } projectKey)
        {
            var key = projectKey.ToUpperInvariant();
            projectId = await db.Projects.Where(p => p.Key == key).Select(p => (Guid?)p.Id).SingleOrDefaultAsync(ct);
            if (projectId is null)
            {
                return new IssueError("project_not_found", $"No project {projectKey}.");
            }
        }

        var milestone = new Milestone
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.RequireWorkspaceId(),
            ProjectId = projectId,
            Name = request.Name.Trim(),
            Date = request.Date,
            CreatedAt = clock.GetUtcNow(),
        };
        db.Milestones.Add(milestone);
        await db.SaveChangesAsync(ct);
        await realtime.ChangedAsync(ChangeTopic.Plan);
        return new MilestoneResponse(milestone.Id, milestone.Name, milestone.Date, request.Project?.ToUpperInvariant());
    }

    public async Task<IssueResult<MilestoneResponse>> UpdateMilestoneAsync(Guid id, UpdateMilestoneRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var milestone = await db.Milestones.SingleOrDefaultAsync(m => m.Id == id, ct);
        if (milestone is null)
        {
            return new IssueError("milestone_not_found", $"No milestone {id}.");
        }

        milestone.Name = request.Name?.Trim() ?? milestone.Name;
        milestone.Date = request.Date ?? milestone.Date;
        await db.SaveChangesAsync(ct);
        await realtime.ChangedAsync(ChangeTopic.Plan);
        var projectKey = await db.Projects.Where(p => p.Id == milestone.ProjectId).Select(p => p.Key).FirstOrDefaultAsync(ct);
        return new MilestoneResponse(milestone.Id, milestone.Name, milestone.Date, projectKey);
    }

    public async Task<bool> DeleteMilestoneAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deleted = await db.Milestones.Where(m => m.Id == id).ExecuteDeleteAsync(ct) > 0;
        if (deleted)
        {
            await realtime.ChangedAsync(ChangeTopic.Plan);
        }

        return deleted;
    }

    /// <summary>Demand and supply for one month of one project. Mirrored in web/src/features/plan/demand.ts.</summary>
    private static PlanMonth Month(DateOnly month, IReadOnlyList<PlanCard> cards, IReadOnlyList<CapacityEntry> steps, decimal hoursPerWeek)
    {
        var monthEnd = month.AddMonths(1).AddDays(-1);
        decimal demand = 0;
        foreach (var card in cards)
        {
            if (card.CountedEffort <= 0 || card.StartDate is not { } s || card.DueDate is not { } d || d < s)
            {
                continue;
            }

            var overlapStart = s > month ? s : month;
            var overlapEnd = d < monthEnd ? d : monthEnd;
            if (overlapEnd < overlapStart)
            {
                continue;
            }

            var spanDays = d.DayNumber - s.DayNumber + 1;
            var overlapDays = overlapEnd.DayNumber - overlapStart.DayNumber + 1;
            demand += card.CountedEffort * overlapDays / spanDays;
        }

        var people = steps.LastOrDefault(e => e.FromMonth <= month)?.People ?? 0;
        var perPerson = HoursPerPerson(month, hoursPerWeek);
        var supply = people * perPerson;
        return new PlanMonth(month, Round(demand), Round(supply), Round(demand / perPerson), people);
    }

    /// <summary>Hours one person gives in a month: hours per week × days in month / 7 (ADR-008).</summary>
    private static decimal HoursPerPerson(DateOnly month, decimal hoursPerWeek) =>
        hoursPerWeek * DateTime.DaysInMonth(month.Year, month.Month) / 7m;

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static DateOnly MonthOf(DateOnly date) => new(date.Year, date.Month, 1);

    private static IEnumerable<DateOnly> Months(DateOnly from, DateOnly to)
    {
        for (var m = from; m <= to; m = m.AddMonths(1))
        {
            yield return m;
        }
    }
}
