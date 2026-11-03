using Kupro.Api.Data;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kupro.Api.Features.Sprints;

public enum LedgerKind
{
    Baseline,
    Added,
    Removed,
    Estimated,
    Completed,
    Reopened,
    Reassigned,
    Canceled,
    Carried,
    Split,
    Backlogged,
}

/// <summary>
/// One change to an issue while its sprint is active (ADR-020): hours of scope and remaining work it added or took away.
/// Append-only. Burndown and the sprint report are sums over these rows.
/// </summary>
public sealed class SprintLedgerEntry : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SprintId { get; set; }
    public Guid IssueId { get; set; }
    public Guid? AssigneeId { get; set; }
    public DateTimeOffset At { get; set; }
    public LedgerKind Kind { get; set; }
    public decimal ScopeDelta { get; set; }
    public decimal RemainingDelta { get; set; }
}

internal sealed class SprintLedgerEntryConfiguration : IEntityTypeConfiguration<SprintLedgerEntry>
{
    public void Configure(EntityTypeBuilder<SprintLedgerEntry> builder)
    {
        builder.ToTable("sprint_ledger");
        builder.HasOne<Sprint>().WithMany().HasWorkspaceForeignKey(l => l.SprintId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Issue>().WithMany().HasWorkspaceForeignKey(l => l.IssueId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Member>().WithMany().HasWorkspaceForeignKey(l => l.AssigneeId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(l => l.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(l => l.ScopeDelta).HasPrecision(10, 2);
        builder.Property(l => l.RemainingDelta).HasPrecision(10, 2);
        builder.HasIndex(l => new { l.WorkspaceId, l.SprintId, l.At });
    }
}

/// <summary>The issue fields the ledger follows.</summary>
public readonly record struct LedgerState(Guid? SprintId, decimal? Effort, IssueState State, Guid? AssigneeId)
{
    /// <summary>Before an issue exists: in no sprint, so its first state reads as an add.</summary>
    public static readonly LedgerState None = new(null, null, IssueState.Backlog, null);

    public static LedgerState Of(Issue issue) => new(issue.SprintId, issue.Effort, issue.State, issue.AssigneeId);

    /// <summary>Canceled issues leave scope; unestimated issues count 0.</summary>
    public decimal Scope => State == IssueState.Canceled ? 0 : Effort ?? 0;

    /// <summary>In Review is still remaining; Done and Canceled are not.</summary>
    public decimal Remaining => State is IssueState.Done or IssueState.Canceled ? 0 : Effort ?? 0;
}

/// <summary>
/// Adds ledger rows to the caller's DbContext so they commit with the change itself, like <c>ActivityLog</c> (ADR-020
/// IMP-002). Every write to an issue's sprint, effort, state or assignee must pass through here; a test guard fails on
/// any save that misses it.
/// </summary>
public sealed class SprintLedger(WorkspaceContext workspace, TimeProvider clock)
{
    /// <summary>
    /// Rows for issues whose sprint, before or after, is active. Reads sprint states only when one is involved. The caller
    /// resolved its sprints before this lock, so a sprint that closed in between is caught here, and the caller must not save.
    /// </summary>
    public async Task<IssueError?> RecordAsync(
        KuproDbContext db, IEnumerable<(Guid IssueId, LedgerState Before, LedgerState After)> changes, CancellationToken ct)
    {
        var changed = changes.Where(c => c.Before != c.After).ToList();
        var sprintIds = changed.SelectMany(c => new[] { c.Before.SprintId, c.After.SprintId }).OfType<Guid>().Distinct().ToList();
        if (sprintIds.Count == 0)
        {
            return null;
        }

        var locked = await LockSprintsAsync(db, sprintIds, ct);
        if (changed.Any(c => c.After.SprintId is { } to && to != c.Before.SprintId && locked.GetValueOrDefault(to) == SprintState.Closed))
        {
            return new IssueError("invalid_sprint", "The sprint closed while this was being saved.");
        }

        var active = locked.Where(s => s.Value == SprintState.Active).Select(s => s.Key).ToList();
        foreach (var (issueId, before, after) in changed)
        {
            foreach (var row in Diff(before, after, active))
            {
                Add(db, row.SprintId, issueId, row.AssigneeId, row.Kind, row.Scope, row.Remaining);
            }
        }

        return null;
    }

    /// <summary>
    /// Holds a shared lock on the sprints until the caller's transaction ends. Starting and
    /// closing a sprint lock it exclusively, so whichever comes first, the other sees its result and no row goes missing.
    /// Take it before any other lock in the transaction. Returns the state of each.
    /// </summary>
    public static async Task<Dictionary<Guid, SprintState>> LockSprintsAsync(KuproDbContext db, IReadOnlyCollection<Guid> sprintIds, CancellationToken ct)
    {
        if (sprintIds.Count == 0)
        {
            return [];
        }

        var ids = sprintIds.ToArray();
        // State is filtered here, not in the query: a planned sprint has to be locked too, or it could start unseen.
        var sprints = await db.Sprints.FromSql($"SELECT * FROM sprints WHERE id = ANY({ids}) ORDER BY id FOR SHARE")
            .AsNoTracking().Select(s => new { s.Id, s.State }).ToListAsync(ct);
        return sprints.ToDictionary(s => s.Id, s => s.State);
    }

    /// <summary>A change inside a sprint the caller knows is active, without a lookup.</summary>
    public void Record(KuproDbContext db, Guid issueId, LedgerState before, LedgerState after)
    {
        foreach (var row in Diff(before, after, [after.SprintId!.Value]))
        {
            Add(db, row.SprintId, issueId, row.AssigneeId, row.Kind, row.Scope, row.Remaining);
        }
    }

    /// <summary>A close outcome: hours leaving the closing sprint. Left out of the burndown lines, fed to the summary.</summary>
    public void RecordLeaving(KuproDbContext db, Guid sprintId, Issue issue, LedgerKind kind, decimal hours) =>
        Add(db, sprintId, issue.Id, issue.AssigneeId, kind, -hours, -hours);

    /// <summary>What the sprint starts with: one row per issue in it.</summary>
    public async Task RecordBaselineAsync(KuproDbContext db, Guid sprintId, CancellationToken ct)
    {
        var issues = await db.Issues.AsNoTracking().Where(i => i.SprintId == sprintId)
            .Select(i => new { i.Id, State = new LedgerState(i.SprintId, i.Effort, i.State, i.AssigneeId) })
            .ToListAsync(ct);
        foreach (var issue in issues)
        {
            Add(db, sprintId, issue.Id, issue.State.AssigneeId, LedgerKind.Baseline, issue.State.Scope, issue.State.Remaining);
        }
    }

    /// <summary>
    /// The rows for one change. Applied in a fixed order (assignee, effort, state) so each row carries the assignee
    /// and hours in force at that step, and the deltas always sum to the difference between the two states.
    /// </summary>
    internal static IEnumerable<LedgerRow> Diff(LedgerState before, LedgerState after, IReadOnlyCollection<Guid> active)
    {
        if (before.SprintId != after.SprintId)
        {
            if (before.SprintId is { } from && active.Contains(from))
            {
                yield return new(from, before.AssigneeId, LedgerKind.Removed, -before.Scope, -before.Remaining);
            }

            if (after.SprintId is { } to && active.Contains(to))
            {
                yield return new(to, after.AssigneeId, LedgerKind.Added, after.Scope, after.Remaining);
            }

            yield break;
        }

        if (before.SprintId is not { } sprint || !active.Contains(sprint))
        {
            yield break;
        }

        var step = before;
        if (after.AssigneeId != step.AssigneeId)
        {
            yield return new(sprint, step.AssigneeId, LedgerKind.Reassigned, -step.Scope, -step.Remaining);
            yield return new(sprint, after.AssigneeId, LedgerKind.Reassigned, step.Scope, step.Remaining);
            step = step with { AssigneeId = after.AssigneeId };
        }

        if (after.Effort != step.Effort)
        {
            var next = step with { Effort = after.Effort };
            yield return new(sprint, after.AssigneeId, LedgerKind.Estimated, next.Scope - step.Scope, next.Remaining - step.Remaining);
            step = next;
        }

        if (after.State != step.State && StateKind(step.State, after.State) is { } kind)
        {
            yield return new(sprint, after.AssigneeId, kind, after.Scope - step.Scope, after.Remaining - step.Remaining);
        }
    }

    /// <summary>Null between open states (Todo to In Progress): nothing the ledger counts changes.</summary>
    private static LedgerKind? StateKind(IssueState from, IssueState to) => to switch
    {
        IssueState.Done => LedgerKind.Completed,
        IssueState.Canceled => LedgerKind.Canceled,
        _ => from is IssueState.Done or IssueState.Canceled ? LedgerKind.Reopened : null,
    };

    private void Add(KuproDbContext db, Guid sprintId, Guid issueId, Guid? assigneeId, LedgerKind kind, decimal scope, decimal remaining) =>
        db.SprintLedger.Add(new SprintLedgerEntry
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.RequireWorkspaceId(),
            SprintId = sprintId,
            IssueId = issueId,
            AssigneeId = assigneeId,
            At = clock.GetUtcNow(),
            Kind = kind,
            ScopeDelta = scope,
            RemainingDelta = remaining,
        });
}

internal readonly record struct LedgerRow(Guid SprintId, Guid? AssigneeId, LedgerKind Kind, decimal Scope, decimal Remaining);
