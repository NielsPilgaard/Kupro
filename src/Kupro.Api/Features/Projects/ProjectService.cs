using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using Kupro.Api.Data;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Kupro.Api.Features.Projects;

/// <param name="Key">Issue key prefix: 2-10 uppercase letters/digits starting with a letter, e.g. <c>KUP</c>.</param>
/// <param name="Name">Project name.</param>
/// <param name="Description">Optional Markdown description.</param>
/// <param name="Color">Colour in lists and the delivery plan; picked automatically when omitted.</param>
public sealed record CreateProjectRequest(
    [Required, RegularExpression(ProjectService.KeyPattern)] string Key,
    [Required, StringLength(100, MinimumLength = 1)] string Name,
    [StringLength(2000)] string? Description = null,
    ProjectColor? Color = null);

/// <param name="Key">New key; only allowed while the project has no issues.</param>
/// <param name="Name">New name.</param>
/// <param name="Description">New description.</param>
/// <param name="Color">New colour.</param>
public sealed record UpdateProjectRequest(
    [RegularExpression(ProjectService.KeyPattern)] string? Key = null,
    [StringLength(100, MinimumLength = 1)] string? Name = null,
    [StringLength(2000)] string? Description = null,
    ProjectColor? Color = null);

/// <param name="Id">Project id.</param>
/// <param name="Key">Issue key prefix.</param>
/// <param name="Name">Project name.</param>
/// <param name="Description">Markdown description.</param>
/// <param name="Color">Display colour.</param>
/// <param name="IssueCount">Issues created so far (including deleted ones; it is the key counter).</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record ProjectResponse(
    Guid Id, string Key, string Name, string? Description, ProjectColor Color, int IssueCount, DateTimeOffset CreatedAt);

public enum ProjectChangeResult
{
    Ok,
    NotFound,
    KeyTaken,
    KeyLocked,
}

/// <summary>Projects in the current workspace. Owns the Project entity.</summary>
public sealed class ProjectService(
    IDbContextFactory<KuproDbContext> dbFactory, WorkspaceContext workspace, TimeProvider clock)
{
    public const string KeyPattern = "^[A-Z][A-Z0-9]{1,9}$";

    private static readonly Expression<Func<Project, ProjectResponse>> ToResponse =
        p => new ProjectResponse(p.Id, p.Key, p.Name, p.Description, p.Color, p.IssueCounter, p.CreatedAt);

    private static readonly Func<Project, ProjectResponse> ToResponseFn = ToResponse.Compile();

    public async Task<IReadOnlyList<ProjectResponse>> ListAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Projects.AsNoTracking().OrderBy(p => p.Name).Select(ToResponse).ToListAsync(ct);
    }

    public async Task<ProjectResponse?> GetAsync(string key, CancellationToken ct)
    {
        var normalized = key.ToUpperInvariant();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Projects.AsNoTracking().Where(p => p.Key == normalized).Select(ToResponse)
            .SingleOrDefaultAsync(ct);
    }

    public async Task<(ProjectChangeResult Result, ProjectResponse? Project)> CreateAsync(
        CreateProjectRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Projects.AnyAsync(p => p.Key == request.Key, ct))
        {
            return (ProjectChangeResult.KeyTaken, null);
        }

        var count = await db.Projects.CountAsync(ct);
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.RequireWorkspaceId(),
            Key = request.Key,
            Name = request.Name.Trim(),
            Description = request.Description,
            Color = request.Color ?? (ProjectColor)(count % Enum.GetValues<ProjectColor>().Length),
            CreatedAt = clock.GetUtcNow(),
        };
        db.Projects.Add(project);

        return await TrySaveAsync(db, ct)
            ? (ProjectChangeResult.Ok, ToResponseFn(project))
            : (ProjectChangeResult.KeyTaken, null);
    }

    public async Task<(ProjectChangeResult Result, ProjectResponse? Project)> UpdateAsync(
        string key, UpdateProjectRequest request, CancellationToken ct)
    {
        var normalized = key.ToUpperInvariant();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var project = await db.Projects.SingleOrDefaultAsync(p => p.Key == normalized, ct);
        if (project is null)
        {
            return (ProjectChangeResult.NotFound, null);
        }

        if (request.Key is { } newKey && newKey != project.Key)
        {
            // Issue keys (KUP-12) are permanent references in commits, docs and agent memory.
            if (project.IssueCounter > 0)
            {
                return (ProjectChangeResult.KeyLocked, null);
            }

            project.Key = newKey;
        }

        project.Name = request.Name?.Trim() ?? project.Name;
        project.Description = request.Description ?? project.Description;
        project.Color = request.Color ?? project.Color;

        return await TrySaveAsync(db, ct)
            ? (ProjectChangeResult.Ok, ToResponseFn(project))
            : (ProjectChangeResult.KeyTaken, null);
    }

    /// <summary>
    /// Reserves <paramref name="count"/> issue numbers atomically and returns the last one. Called by IssueService;
    /// a failed insert afterwards leaves a gap, which is fine for keys.
    /// </summary>
    public static async Task<int> AllocateIssueNumbersAsync(
        KuproDbContext db, Guid projectId, int count, CancellationToken ct)
    {
        var last = await db.Database.SqlQuery<int>($"""
            UPDATE projects SET issue_counter = issue_counter + {count}
            WHERE id = {projectId}
            RETURNING issue_counter AS "Value"
            """).ToListAsync(ct);
        return last.Single();
    }

    private static async Task<bool> TrySaveAsync(KuproDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
        catch (DbUpdateConcurrencyException)
        {
            // IssueCounter is a concurrency token: an issue was created while the key was being changed.
            return false;
        }
    }
}
