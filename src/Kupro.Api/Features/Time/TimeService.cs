using System.Linq.Expressions;
using Kupro.Api.Data;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Api.Features.Time;

/// <summary>
/// Time registration (ADR-021, task 034). Owns TimeEntry and TimeEntryChange. A member logs, edits and deletes their
/// own entries; Owners read and export everyone's but edit none. Entries on a Done or Canceled issue are locked.
/// </summary>
public sealed class TimeService(
    IDbContextFactory<KuproDbContext> dbFactory, WorkspaceContext workspace, IssueService issues, TimeProvider clock)
{
    private const string CopenhagenId = "Europe/Copenhagen";
    private static readonly TimeZoneInfo Copenhagen = TimeZoneInfo.FindSystemTimeZoneById(CopenhagenId);

    /// <summary>Today in Europe/Copenhagen: the latest day an entry may be on.</summary>
    public DateOnly Today => LocalDay(clock.GetUtcNow());

    public static DateOnly LocalDay(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Copenhagen).DateTime);

    /// <summary>Monday of the week <paramref name="day"/> is in.</summary>
    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    // ---------------------------------------------------------------- writes

    public async Task<IssueResult<TimeEntryResponse>> LogAsync(LogTimeRequest request, CancellationToken ct)
    {
        if (request.State is { } state && state is not (IssueState.Done or IssueState.Canceled))
        {
            return new IssueError("invalid_state", "Logging time can close the issue as done or canceled, nothing else.");
        }

        var date = request.Date ?? Today;
        if (DateError(date) is { } dateError)
        {
            return dateError;
        }

        await using var read = await dbFactory.CreateDbContextAsync(ct);
        if (await WritableError(read, ct) is { } offError)
        {
            return offError;
        }

        var person = await PersonAsync(read, ct);
        if (person is null)
        {
            return NotAPerson;
        }

        var resolved = await IssueKeys.ResolveAsync(read, [request.Issue], ct);
        if (!resolved.TryGetValue(request.Issue.Trim(), out var key))
        {
            return IssueError.NotFound(request.Issue);
        }

        var issueId = await read.Issues.Where(i => i.Key == key).Select(i => i.Id).SingleAsync(ct);
        var id = Guid.CreateVersion7();
        var closing = request.State is not null;

        Task<IssueError?> Write(KuproDbContext db) => AddAsync(db, new TimeEntry
        {
            Id = id,
            WorkspaceId = workspace.RequireWorkspaceId(),
            MemberId = person.Value,
            IssueId = issueId,
            Date = date,
            Minutes = request.Minutes,
            Note = Note(request.Note),
            Via = workspace.Surface,
        }, closing, ct);

        if (closing)
        {
            // The entry goes in first, while the issue is still open; the state change locks it in the same save.
            var closed = await issues.UpdateWithAsync([new IssueUpdate(key, new UpdateIssueRequest(State: request.State))], Write, ct);
            if (closed.Error is { } closeError)
            {
                return closeError;
            }
        }
        else if (await InTransactionAsync(Write, ct) is { } error)
        {
            return error;
        }

        return (await GetAsync(id, ct))!;
    }

    public async Task<IssueResult<TimeEntryResponse>> UpdateAsync(Guid id, UpdateTimeEntryRequest request, CancellationToken ct)
    {
        if (request.Date is { } date && DateError(date) is { } dateError)
        {
            return dateError;
        }

        var error = await InTransactionAsync(async db =>
        {
            var (entry, found) = await OwnEntryAsync(db, id, ct);
            if (found is not null)
            {
                return found;
            }

            if (await LockError(db, entry!.IssueId, forUpdate: false, ct) is { } locked)
            {
                return locked;
            }

            var change = new TimeEntryChange
            {
                OldDate = entry.Date,
                OldMinutes = entry.Minutes,
                OldNote = entry.Note,
            };
            var note = request.Note is null ? entry.Note : Note(request.Note);
            var (newDate, newMinutes) = (request.Date ?? entry.Date, request.Minutes ?? entry.Minutes);
            if (newDate == entry.Date && newMinutes == entry.Minutes && note == entry.Note)
            {
                return null;
            }

            if (await DayError(db, entry.MemberId, newDate, newMinutes, except: entry.Id, ct) is { } dayError)
            {
                return dayError;
            }

            (entry.Date, entry.Minutes, entry.Note, entry.UpdatedAt) = (newDate, newMinutes, note, clock.GetUtcNow());
            (change.NewDate, change.NewMinutes, change.NewNote) = (newDate, newMinutes, note);
            Record(db, entry, TimeChangeKind.Updated, change);
            await db.SaveChangesAsync(ct);
            return null;
        }, ct);

        return error ?? (IssueResult<TimeEntryResponse>)(await GetAsync(id, ct))!;
    }

    public Task<IssueError?> DeleteAsync(Guid id, CancellationToken ct) => InTransactionAsync(async db =>
    {
        var (entry, found) = await OwnEntryAsync(db, id, ct);
        if (found is not null)
        {
            return found;
        }

        if (await LockError(db, entry!.IssueId, forUpdate: false, ct) is { } locked)
        {
            return locked;
        }

        Record(db, entry, TimeChangeKind.Deleted, new TimeEntryChange { OldDate = entry.Date, OldMinutes = entry.Minutes, OldNote = entry.Note });
        db.TimeEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return null;
    }, ct);

    /// <summary>Adds an entry made in Kupro: checks the lock and the day's cap, and records the creation.</summary>
    private async Task<IssueError?> AddAsync(KuproDbContext db, TimeEntry entry, bool closing, CancellationToken ct)
    {
        if (await LockError(db, entry.IssueId, forUpdate: closing, ct) is { } locked)
        {
            return locked;
        }

        if (await DayError(db, entry.MemberId, entry.Date, entry.Minutes, except: null, ct) is { } dayError)
        {
            return dayError;
        }

        entry.CreatedAt = entry.UpdatedAt = clock.GetUtcNow();
        db.TimeEntries.Add(entry);
        Record(db, entry, TimeChangeKind.Created, new TimeEntryChange { NewDate = entry.Date, NewMinutes = entry.Minutes, NewNote = entry.Note });
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// An entry an import writes (ADR-021 Import): kept as the source recorded it, so no lock, cap or date check. Added
    /// to the caller's context, so it commits with the import's batch.
    /// </summary>
    internal void AddImported(KuproDbContext db, Guid id, Guid memberId, Guid issueId, DateOnly date, int minutes, string? note, DateTimeOffset createdAt, Guid changedById)
    {
        var entry = new TimeEntry
        {
            Id = id,
            WorkspaceId = workspace.RequireWorkspaceId(),
            MemberId = memberId,
            IssueId = issueId,
            Date = date,
            Minutes = Math.Max(1, minutes),
            Note = Note(note),
            Via = Surface.Import,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        db.TimeEntries.Add(entry);
        db.TimeEntryChanges.Add(new TimeEntryChange
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = entry.WorkspaceId,
            EntryId = entry.Id,
            MemberId = memberId,
            IssueId = issueId,
            ChangedById = changedById,
            Via = Surface.Import,
            Kind = TimeChangeKind.Created,
            At = clock.GetUtcNow(),
            NewDate = date,
            NewMinutes = entry.Minutes,
            NewNote = entry.Note,
        });
    }

    private void Record(KuproDbContext db, TimeEntry entry, TimeChangeKind kind, TimeEntryChange change)
    {
        change.Id = Guid.CreateVersion7();
        change.WorkspaceId = entry.WorkspaceId;
        change.EntryId = entry.Id;
        change.MemberId = entry.MemberId;
        change.IssueId = entry.IssueId;
        change.ChangedById = workspace.MemberId;
        change.Via = workspace.Surface;
        change.Kind = kind;
        change.At = clock.GetUtcNow();
        db.TimeEntryChanges.Add(change);
    }

    /// <summary>
    /// The caller's entry, tracked, ready to change. Someone else's is not found for a member, who can't see it; an
    /// Owner sees it but doesn't edit it (ADR-021 Who sees what). Writes need the setting on.
    /// </summary>
    private async Task<(TimeEntry? Entry, IssueError? Error)> OwnEntryAsync(KuproDbContext db, Guid id, CancellationToken ct)
    {
        if (await WritableError(db, ct) is { } offError)
        {
            return (null, offError);
        }

        var person = await PersonAsync(db, ct);
        var entry = await db.TimeEntries.SingleOrDefaultAsync(e => e.Id == id, ct);
        return entry is null || (entry.MemberId != person && workspace.Role < WorkspaceRole.Owner) ? (null, EntryNotFound(id))
            : entry.MemberId != person ? (null, new IssueError("not_your_entry", "Only the person whose time it is can change an entry."))
            : (entry, null);
    }

    /// <summary>
    /// Refuses while the issue is Done or Canceled. Holds a lock on the issue row until the transaction ends, so the issue
    /// can't close between this check and the save; closing takes it exclusively, as it updates the row next.
    /// </summary>
    private static async Task<IssueError?> LockError(KuproDbContext db, Guid issueId, bool forUpdate, CancellationToken ct)
    {
        var workspaceId = db.CurrentWorkspaceId!.Value;
        var state = forUpdate
            ? await db.Database.SqlQuery<short>($"SELECT state AS \"Value\" FROM issues WHERE workspace_id = {workspaceId} AND id = {issueId} FOR NO KEY UPDATE").ToListAsync(ct)
            : await db.Database.SqlQuery<short>($"SELECT state AS \"Value\" FROM issues WHERE workspace_id = {workspaceId} AND id = {issueId} FOR SHARE").ToListAsync(ct);
        return (IssueState)state.Single() is IssueState.Done or IssueState.Canceled
            ? new IssueError("time_locked", "The issue is done or canceled, so its time is locked. Reopen it to change time on it.")
            : null;
    }

    /// <summary>
    /// A day made in Kupro holds at most 24 hours (ADR-021 Model). Serialized per person and day, so two saves at once
    /// can't both pass the check.
    /// </summary>
    private static async Task<IssueError?> DayError(KuproDbContext db, Guid memberId, DateOnly date, int minutes, Guid? except, CancellationToken ct)
    {
        var bytes = memberId.ToByteArray();
        var lockKey = BitConverter.ToInt64(bytes, 0) ^ BitConverter.ToInt64(bytes, 8) ^ date.DayNumber;
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({lockKey})", ct);
        var logged = await db.TimeEntries.Where(e => e.MemberId == memberId && e.Date == date && e.Id != except).SumAsync(e => e.Minutes, ct);
        return logged + minutes > TimeLimits.MinutesPerDay
            ? new IssueError("day_full", $"{date:yyyy-MM-dd} would hold more than 24 hours ({Hours(logged)} already logged).")
            : null;
    }

    private IssueError? DateError(DateOnly date) =>
        date > Today ? new IssueError("future_date", $"{date:yyyy-MM-dd} is in the future. Log time on today or earlier.") : null;

    private static async Task<IssueError?> WritableError(KuproDbContext db, CancellationToken ct) =>
        await EnabledAsync(db, ct) ? null
            : new IssueError("time_registration_off", "Time registration is off in this workspace. An owner can turn it on in settings.");

    private static Task<bool> EnabledAsync(KuproDbContext db, CancellationToken ct) =>
        db.Workspaces.Where(w => w.Id == db.CurrentWorkspaceId).Select(w => w.TimeRegistration).SingleAsync(ct);

    public async Task<bool> EnabledAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await EnabledAsync(db, ct);
    }

    /// <summary>
    /// The person the caller's time belongs to: themselves, or for an agent token, the person it acts for (ADR-021). Null
    /// when that person isn't an active human member.
    /// </summary>
    private async Task<Guid?> PersonAsync(KuproDbContext db, CancellationToken ct) =>
        workspace.AgentName is null
            ? workspace.MemberId
            : await db.Members.Where(m => m.UserId == workspace.UserId && m.Kind == MemberKind.Human && m.Status == MemberStatus.Active)
                .Select(m => (Guid?)m.Id).SingleOrDefaultAsync(ct);

    private async Task<IssueError?> InTransactionAsync(Func<KuproDbContext, Task<IssueError?>> write, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        IssueError? error = null;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            error = await write(db);
            if (error is null)
            {
                await tx.CommitAsync(ct);
            }
        });
        return error;
    }

    // ---------------------------------------------------------------- reads

    /// <summary>The caller's entries and day totals from <paramref name="from"/> to <paramref name="to"/>, both included.</summary>
    public async Task<IssueResult<MyTime>> MineAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var first = from ?? WeekStart(to ?? Today);
        var last = from is not null && to is { } end ? end : first.AddDays(6);
        if (RangeError(first, last, TimeLimits.MaxListDays) is { } rangeError)
        {
            return rangeError;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var person = await PersonAsync(db, ct);
        if (person is null)
        {
            return NotAPerson;
        }

        var entries = await Project(db, db.TimeEntries.AsNoTracking()
                .Where(e => e.MemberId == person && e.Date >= first && e.Date <= last)
                .OrderBy(e => e.Date).ThenBy(e => e.CreatedAt))
            .ToListAsync(ct);
        var byDay = entries.GroupBy(e => e.Date).ToDictionary(g => g.Key, g => g.Sum(e => e.Minutes));
        var days = Enumerable.Range(0, last.DayNumber - first.DayNumber + 1)
            .Select(n => first.AddDays(n))
            .Select(d => new TimeDay(d, byDay.GetValueOrDefault(d)))
            .ToList();
        return new MyTime(first, last, entries, days, entries.Sum(e => e.Minutes));
    }

    /// <summary>Time on an issue: the caller's own, and for Owners the total and each person's share (ADR-021).</summary>
    public async Task<IssueResult<IssueTime>> ForIssueAsync(string key, CancellationToken ct) =>
        await ForIssuesAsync([key], ct) is [var time, ..] ? time : IssueError.NotFound(key);

    /// <summary>As <see cref="ForIssueAsync"/> for several issues, with the same queries however many.</summary>
    public async Task<IReadOnlyList<IssueTime>> ForIssuesAsync(IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var resolved = await IssueKeys.ResolveAsync(db, keys, ct);
        var wanted = resolved.Values.Distinct().ToList();
        var found = await db.Issues.AsNoTracking().Where(i => wanted.Contains(i.Key))
            .Select(i => new { i.Id, i.Key, i.State }).ToListAsync(ct);
        var ids = found.Select(i => i.Id).ToList();
        var person = await PersonAsync(db, ct);
        var owner = workspace.Role == WorkspaceRole.Owner;

        var shares = await db.TimeEntries.AsNoTracking()
            .Where(e => ids.Contains(e.IssueId) && (owner || e.MemberId == person))
            .GroupBy(e => new { e.IssueId, e.MemberId })
            .Select(g => new { g.Key.IssueId, g.Key.MemberId, Minutes = g.Sum(e => e.Minutes) })
            .ToListAsync(ct);
        var memberIds = shares.Select(s => s.MemberId).Distinct().ToList();
        var members = owner
            ? await db.Members.AsNoTracking().Where(m => memberIds.Contains(m.Id)).Select(MemberRefOf(db)).ToDictionaryAsync(m => m.Id, ct)
            : [];

        return keys.Select(k => resolved.GetValueOrDefault(k.Trim()))
            .Select(k => found.FirstOrDefault(i => i.Key == k))
            .Where(i => i is not null)
            .DistinctBy(i => i!.Id)
            .Select(i =>
            {
                var mine = shares.Where(s => s.IssueId == i!.Id).ToList();
                return new IssueTime(
                    i!.Key,
                    i.State is IssueState.Done or IssueState.Canceled,
                    mine.Where(s => s.MemberId == person).Sum(s => s.Minutes),
                    owner ? mine.Sum(s => s.Minutes) : null,
                    owner ? mine.OrderByDescending(s => s.Minutes).Select(s => new MemberTime(members[s.MemberId], s.Minutes)).ToList() : null);
            })
            .ToList();
    }

    /// <summary>Everyone's entries for a range, or one member's (Owners).</summary>
    public async Task<IssueResult<TimeEntryList>> ListAsync(DateOnly from, DateOnly to, Guid? member, CancellationToken ct)
    {
        if (RangeError(from, to, TimeLimits.MaxListDays) is { } rangeError)
        {
            return rangeError;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var items = await Project(db, db.TimeEntries.AsNoTracking()
                .Where(e => e.Date >= from && e.Date <= to && (member == null || e.MemberId == member))
                .OrderBy(e => e.Date).ThenBy(e => e.MemberId).ThenBy(e => e.CreatedAt))
            .ToListAsync(ct);
        return new TimeEntryList(items, items.Sum(e => e.Minutes));
    }

    /// <summary>
    /// The employer's copy (Owners): one row per entry, members who left included. Streamed in date order, so a year
    /// of a large team doesn't sit in memory.
    /// </summary>
    public IssueResult<IAsyncEnumerable<TimeExportRow>> Export(DateOnly from, DateOnly to, Guid? member, CancellationToken ct) =>
        RangeError(from, to, TimeLimits.MaxExportDays) is { } rangeError
            ? rangeError
            : new IssueResult<IAsyncEnumerable<TimeExportRow>>(ExportRows(from, to, member, ct), null);

    private async IAsyncEnumerable<TimeExportRow> ExportRows(
        DateOnly first, DateOnly last, Guid? member, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = from e in db.TimeEntries.AsNoTracking()
                   where e.Date >= first && e.Date <= last && (member == null || e.MemberId == member)
                   join i in db.Issues on e.IssueId equals i.Id
                   join m in db.Members on e.MemberId equals m.Id
                   orderby e.Date, m.Id, e.CreatedAt
                   select new TimeExportRow(
                       e.Date,
                       m.Id,
                       m.DisplayName ?? db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefault() ?? "",
                       db.Users.Where(u => u.Id == m.UserId).Select(u => u.Email).FirstOrDefault() ?? m.PlaceholderEmail,
                       i.Key,
                       i.Title,
                       e.Minutes,
                       e.Note,
                       m.Status == MemberStatus.Deactivated);
        await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(ct))
        {
            yield return row;
        }
    }

    /// <summary>
    /// History of entries, newest first: the caller's own, or for Owners any member's (ADR-021 Reliability). A page of
    /// <paramref name="limit"/>; pass the last id as <paramref name="before"/> for older changes.
    /// </summary>
    public async Task<IssueResult<IReadOnlyList<TimeEntryChangeResponse>>> ChangesAsync(Guid? member, Guid? before, int limit, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var person = await PersonAsync(db, ct);
        var whose = member ?? person;
        if (whose != person && workspace.Role < WorkspaceRole.Owner)
        {
            return new IssueError("insufficient_role", "Only Owners see other people's time.");
        }

        var query = db.TimeEntryChanges.AsNoTracking().Where(c => c.MemberId == whose);
        if (before is { } b)
        {
            var at = await db.TimeEntryChanges.Where(c => c.Id == b).Select(c => (DateTimeOffset?)c.At).SingleOrDefaultAsync(ct);
            if (at is null)
            {
                return new IssueError("invalid_cursor", $"No change {b}.");
            }

            query = query.Where(c => EF.Functions.LessThan(ValueTuple.Create(c.At, c.Id), ValueTuple.Create(at.Value, b)));
        }

        var memberRef = MemberRefOf(db);
        return await query.OrderByDescending(c => c.At).ThenByDescending(c => c.Id).Take(limit)
            .Select(c => new TimeEntryChangeResponse(
                c.Id,
                c.EntryId,
                db.Members.Where(m => m.Id == c.MemberId).Select(memberRef).First(),
                db.Members.Where(m => m.Id == c.ChangedById).Select(memberRef).First(),
                db.Issues.Where(i => i.Id == c.IssueId).Select(i => i.Key).First(),
                c.Kind,
                c.Via,
                c.At,
                c.OldDate,
                c.NewDate,
                c.OldMinutes,
                c.NewMinutes,
                c.OldNote,
                c.NewNote))
            .ToListAsync(ct);
    }

    private async Task<TimeEntryResponse?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Project(db, db.TimeEntries.AsNoTracking().Where(e => e.Id == id)).SingleOrDefaultAsync(ct);
    }

    private static IQueryable<TimeEntryResponse> Project(KuproDbContext db, IQueryable<TimeEntry> entries)
    {
        var memberRef = MemberRefOf(db);
        return from e in entries
               join i in db.Issues on e.IssueId equals i.Id
               select new TimeEntryResponse(
                   e.Id,
                   db.Members.Where(m => m.Id == e.MemberId).Select(memberRef).First(),
                   new IssueRef(i.Key, i.Title, i.Type, i.State),
                   db.Projects.Where(p => p.Id == i.ProjectId).Select(p => p.Key).First(),
                   e.Date,
                   e.Minutes,
                   e.Note,
                   i.State == IssueState.Done || i.State == IssueState.Canceled,
                   e.Via,
                   e.CreatedAt,
                   e.UpdatedAt);
    }

    private static Expression<Func<Member, MemberRef>> MemberRefOf(KuproDbContext db) => m => new MemberRef(
        m.Id,
        m.Kind == MemberKind.Agent
            ? m.AgentName!
            : m.DisplayName ?? db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefault()!,
        m.Kind);

    // ---------------------------------------------------------------- retention

    /// <summary>
    /// Deletes entries and their history older than the retention period, in every workspace (ADR-021 Retention): by
    /// the day recorded, not by when the row changed.
    /// </summary>
    public static async Task<int> PurgeExpiredAsync(KuproDbContext db, DateOnly today, CancellationToken ct)
    {
        var cutoff = today.AddYears(-TimeLimits.RetentionYears);

        // The job runs outside any workspace and deletes across all of them by date only.
        var entries = await db.TimeEntries.IgnoreQueryFilters([KuproDbContext.TenantFilter])
            .Where(e => e.Date < cutoff).ExecuteDeleteAsync(ct);
        await db.TimeEntryChanges.IgnoreQueryFilters([KuproDbContext.TenantFilter])
            .Where(c => (c.NewDate == null || c.NewDate < cutoff) && (c.OldDate == null || c.OldDate < cutoff))
            .ExecuteDeleteAsync(ct);
        return entries;
    }

    // ---------------------------------------------------------------- helpers

    private static IssueError? RangeError(DateOnly from, DateOnly to, int maxDays) =>
        to < from ? new IssueError("invalid_range", "`to` is before `from`.")
        : to.DayNumber - from.DayNumber + 1 > maxDays ? new IssueError("invalid_range", $"A range covers at most {maxDays} days.")
        : null;

    private static string? Note(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    private static string Hours(int minutes) => $"{minutes / 60}h{minutes % 60:00}m";

    private static IssueError EntryNotFound(Guid id) => new("time_entry_not_found", $"No time entry {id}.");

    private static readonly IssueError NotAPerson = new("not_a_person", "Time belongs to a person; this token acts for no active member.");
}
