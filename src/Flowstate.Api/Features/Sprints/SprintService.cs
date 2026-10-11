using System.ComponentModel.DataAnnotations;
using Flowstate.Api.Data;
using Flowstate.Api.Features.Issues;
using Flowstate.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Flowstate.Api.Features.Sprints;

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

public enum UnfinishedDestination
{
    /// <summary>The project's next planned sprint (earliest start).</summary>
    Next,

    /// <summary>Out of any sprint; state is kept.</summary>
    Backlog,
}

/// <param name="MoveUnfinishedTo">Where open issues go: <c>next</c> sprint or <c>backlog</c>.</param>
public sealed record CloseSprintRequest(UnfinishedDestination MoveUnfinishedTo = UnfinishedDestination.Next);

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

/// <param name="Sprint">The closed sprint.</param>
/// <param name="Moved">How many unfinished issues moved.</param>
/// <param name="MovedTo">Sprint they moved to; null when they went to the backlog.</param>
public sealed record CloseSprintResponse(SprintResponse Sprint, int Moved, SprintResponse? MovedTo);

/// <summary>Sprints per project (task 010). Owns Sprint; issue membership changes go through IssueService.</summary>
public sealed class SprintService(
    IDbContextFactory<FlowstateDbContext> dbFactory,
    WorkspaceContext workspace,
    IssueService issues,
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
        if (request.EndDate < request.StartDate)
        {
            return new IssueError("invalid_dates", "The sprint ends before it starts.");
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
        if (sprint.EndDate < sprint.StartDate)
        {
            return new IssueError("invalid_dates", "The sprint ends before it starts.");
        }

        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Starts a planned sprint. Fails while the project has another active sprint.</summary>
    public async Task<IssueResult<SprintResponse>> StartAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sprint = await db.Sprints.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (sprint is null)
        {
            return NotFound(id);
        }

        if (sprint.State != SprintState.Planned)
        {
            return new IssueError("sprint_not_planned", $"{sprint.Name} is {sprint.State}; only planned sprints start.");
        }

        sprint.State = SprintState.Active;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new IssueError("sprint_already_active", "The project already has an active sprint. Close it first.");
        }

        return await GetAsync(id, ct);
    }

    /// <summary>Closes the active sprint and moves its unfinished issues to the next sprint or the backlog.</summary>
    public async Task<IssueResult<CloseSprintResponse>> CloseAsync(Guid id, CloseSprintRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sprint = await db.Sprints.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (sprint is null)
        {
            return NotFound(id);
        }

        if (sprint.State != SprintState.Active)
        {
            return new IssueError("sprint_not_active", $"{sprint.Name} isn't active.");
        }

        Guid? target = null;
        if (request.MoveUnfinishedTo == UnfinishedDestination.Next)
        {
            target = await db.Sprints
                .Where(s => s.ProjectId == sprint.ProjectId && s.State == SprintState.Planned)
                .OrderBy(s => s.StartDate).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
            if (target is null)
            {
                return new IssueError("no_next_sprint", "There is no planned sprint to move unfinished issues to. Create one, or move them to the backlog.");
            }
        }

        var moved = await issues.MoveUnfinishedAsync(sprint.Id, target, ct);
        sprint.State = SprintState.Closed;
        sprint.ClosedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        var closed = (await GetAsync(id, ct)).Value!;
        var next = target is { } t ? (await GetAsync(t, ct)).Value : null;
        return new CloseSprintResponse(closed, moved, next);
    }

    private static IQueryable<SprintResponse> Project(FlowstateDbContext db, IQueryable<Sprint> sprints) =>
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
