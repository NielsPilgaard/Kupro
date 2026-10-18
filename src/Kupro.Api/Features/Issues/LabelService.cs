using Kupro.Api.Data;
using Kupro.Api.Features.Projects;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Kupro.Api.Features.Issues;

/// <summary>Workspace labels. Owns the Label entity; issues reference labels by name.</summary>
public sealed class LabelService(
    IDbContextFactory<KuproDbContext> dbFactory, WorkspaceContext workspace, TimeProvider clock)
{
    public const string Bug = "bug";

    public static string Normalize(string name) => name.Trim().ToLowerInvariant();

    public async Task<IReadOnlyList<LabelResponse>> ListAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Labels.AsNoTracking()
            .OrderBy(l => l.Name)
            .Select(l => new LabelResponse(l.Id, l.Name, l.Color, db.IssueLabels.Count(il => il.LabelId == l.Id)))
            .ToListAsync(ct);
    }

    /// <summary>Renames or recolours a label. Null if not found; error code if the new name is taken.</summary>
    public async Task<IssueResult<LabelResponse>> UpdateAsync(string name, UpdateLabelRequest request, CancellationToken ct)
    {
        var current = Normalize(name);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var label = await db.Labels.SingleOrDefaultAsync(l => l.Name == current, ct);
        if (label is null)
        {
            return new IssueError("label_not_found", $"No label '{current}'.");
        }

        label.Name = request.Name is { } newName ? Normalize(newName) : label.Name;
        label.Color = request.Color ?? label.Color;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new IssueError("label_taken", $"A label '{label.Name}' already exists.");
        }

        var count = await db.IssueLabels.CountAsync(il => il.LabelId == label.Id, ct);
        return new LabelResponse(label.Id, label.Name, label.Color, count);
    }

    /// <summary>
    /// Label ids for the given names, adding missing labels to <paramref name="db"/> so they commit with the issue
    /// change that introduced them.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ResolveAsync(KuproDbContext db, IEnumerable<string> names, CancellationToken ct)
    {
        var wanted = names.Select(Normalize).Where(n => n.Length is > 0 and <= 50).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var existing = await db.Labels.Where(l => wanted.Contains(l.Name)).Select(l => new { l.Id, l.Name }).ToListAsync(ct);
        var tracked = db.ChangeTracker.Entries<Label>().Select(e => e.Entity).Where(l => wanted.Contains(l.Name)).ToList();
        var ids = existing.Select(l => l.Id).Concat(tracked.Select(l => l.Id)).ToList();

        foreach (var missing in wanted.Except(existing.Select(l => l.Name)).Except(tracked.Select(l => l.Name)))
        {
            var label = new Label
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = workspace.RequireWorkspaceId(),
                Name = missing,
                Color = missing == Bug ? ProjectColor.Brick : PickColor(missing),
                CreatedAt = clock.GetUtcNow(),
            };
            db.Labels.Add(label);
            ids.Add(label.Id);
        }

        return ids;
    }

    private static ProjectColor PickColor(string name)
    {
        var colors = Enum.GetValues<ProjectColor>();
        var hash = name.Aggregate(17, (h, c) => unchecked((h * 31) + c));
        return colors[(hash & int.MaxValue) % colors.Length];
    }
}
