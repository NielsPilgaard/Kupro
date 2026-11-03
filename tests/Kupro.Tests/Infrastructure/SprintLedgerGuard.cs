using Kupro.Api.Data;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Sprints;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Kupro.Tests.Infrastructure;

/// <summary>
/// ADR-020 IMP-002: fails any save that changes an issue's sprint, effort, state or assignee, with an active sprint
/// involved, without a ledger row for that issue in the same save. Installed on every test API, so each test that
/// reaches a write path also guards it, including write paths added later.
/// </summary>
public sealed class SprintLedgerGuard : SaveChangesInterceptor
{
    private static readonly string[] Followed = [nameof(Issue.SprintId), nameof(Issue.Effort), nameof(Issue.State), nameof(Issue.AssigneeId)];

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var db = (KuproDbContext)eventData.Context!;
        db.ChangeTracker.DetectChanges();

        var changed = db.ChangeTracker.Entries<Issue>()
            .Where(e => e.State == EntityState.Added
                || (e.State == EntityState.Modified && Followed.Any(p => e.Property(p).IsModified && !Equals(e.Property(p).OriginalValue, e.Property(p).CurrentValue))))
            .Select(e => new
            {
                e.Entity.Key,
                e.Entity.Id,
                Sprints = new[] { e.State == EntityState.Added ? null : (Guid?)e.Property(nameof(Issue.SprintId)).OriginalValue, e.Entity.SprintId },
            })
            .Where(i => i.Sprints.Any(s => s is not null))
            .ToList();
        if (changed.Count == 0)
        {
            return result;
        }

        var ids = changed.SelectMany(i => i.Sprints).OfType<Guid>().Distinct().ToList();
        // Checks only: the tenant filter may not be set in a test that writes directly to the context.
        var active = await db.Sprints.IgnoreQueryFilters().AsNoTracking()
            .Where(s => ids.Contains(s.Id) && s.State == SprintState.Active).Select(s => s.Id).ToListAsync(cancellationToken);
        var recorded = db.ChangeTracker.Entries<SprintLedgerEntry>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.IssueId).ToHashSet();
        var missing = changed.Where(i => i.Sprints.OfType<Guid>().Any(active.Contains) && !recorded.Contains(i.Id)).Select(i => i.Key).ToList();
        return missing.Count == 0
            ? result
            : throw new InvalidOperationException($"Issues changed in an active sprint without sprint ledger rows: {string.Join(", ", missing)}.");
    }
}
