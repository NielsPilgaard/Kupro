using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using Kupro.Api.Data;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Realtime;
using Kupro.Api.Features.Sprints;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Kupro.Api.Features.Issues;

/// <summary>
/// Issues in the current workspace (task 004, ADR-008): create, read, list, update, move and relations. Owns Issue,
/// IssueLabel and IssueRelation, and records an activity entry for every change.
/// </summary>
public sealed class IssueService(
    IDbContextFactory<KuproDbContext> dbFactory,
    WorkspaceContext workspace,
    LabelService labels,
    ActivityLog activity,
    SprintLedger ledger,
    RealtimeEvents realtime,
    TimeProvider clock)
{
    public const string Me = "me";
    public const string Unassigned = "none";
    public const string ActiveSprint = "active";

    internal const string SplitFromKind = "split_from";
    internal const string SplitIntoKind = "split_into";

    // ---------------------------------------------------------------- reads

    public async Task<IssueDetail?> GetAsync(string key, CancellationToken ct)
    {
        var normalized = NormalizeKey(key);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Issues.AsNoTracking().Where(i => i.Key == normalized).Select(SummaryOf(db)).SingleOrDefaultAsync(ct);
        if (s is null)
        {
            return null;
        }

        var row = await db.Issues.AsNoTracking()
            .Where(i => i.Id == s.Id)
            .Select(i => new
            {
                ProjectKey = db.Projects.Where(p => p.Id == i.ProjectId).Select(p => p.Key).First(),
                i.Description,
                Parent = db.Issues.Where(p => p.Id == i.ParentId).Select(p => new IssueRef(p.Key, p.Title, p.Type, p.State))
                    .FirstOrDefault(),
                Children = db.Issues.Where(c => c.ParentId == i.Id).OrderBy(c => c.Rank)
                    .Select(c => new IssueRef(c.Key, c.Title, c.Type, c.State)).ToList(),
                ChildEffort = db.Issues.Where(c => c.ParentId == i.Id).Sum(c => c.Effort),
                Outgoing = db.IssueRelations.Where(r => r.SourceId == i.Id)
                    .Join(db.Issues, r => r.TargetId, o => o.Id,
                        (r, o) => new { r.Id, r.Type, Other = new IssueRef(o.Key, o.Title, o.Type, o.State) })
                    .ToList(),
                Incoming = db.IssueRelations.Where(r => r.TargetId == i.Id)
                    .Join(db.Issues, r => r.SourceId, o => o.Id,
                        (r, o) => new { r.Id, r.Type, Other = new IssueRef(o.Key, o.Title, o.Type, o.State) })
                    .ToList(),
                CreatedBy = db.Members.Where(m => m.Id == i.CreatedById)
                    .Select(m => new MemberRef(
                        m.Id,
                        m.Kind == MemberKind.Agent
                            ? m.AgentName!
                            : db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefault()!,
                        m.Kind))
                    .First(),
                i.CompletedAt,
                Splits = db.Activity.Where(a => a.IssueId == i.Id && (a.Kind == SplitFromKind || a.Kind == SplitIntoKind))
                    .OrderBy(a => a.CreatedAt).Select(a => new { a.Kind, a.Data }).ToList(),
            })
            .SingleAsync(ct);

        var relations = row.Outgoing.Select(r => new IssueRelationResponse(r.Id, ViewOf(r.Type, outgoing: true), r.Other))
            .Concat(row.Incoming.Select(r => new IssueRelationResponse(r.Id, ViewOf(r.Type, outgoing: false), r.Other)))
            .OrderBy(r => r.Type).ThenBy(r => r.Issue.Key, StringComparer.Ordinal)
            .ToList();
        // An issue can be reopened and split again; the latest link wins.
        var splitKeys = new Dictionary<string, string?>();
        foreach (var split in row.Splits)
        {
            splitKeys[split.Kind] = SplitKey(split.Data);
        }

        var splitRefs = splitKeys.Count == 0 ? [] : await db.Issues.AsNoTracking().Where(i => splitKeys.Values.Contains(i.Key))
            .Select(i => new IssueRef(i.Key, i.Title, i.Type, i.State)).ToListAsync(ct);
        IssueRef? SplitRef(string kind) => splitKeys.TryGetValue(kind, out var other) ? splitRefs.Find(r => r.Key == other) : null;
        return new IssueDetail(s.Id, s.Key, row.ProjectKey, s.Type, s.Title, row.Description, s.State, s.Priority,
            s.Assignee, row.Parent, s.Effort, row.Children.Count > 0 ? row.ChildEffort : null, s.StartDate, s.DueDate,
            s.Labels, row.Children, relations, s.Rank, s.SprintId, row.CreatedBy, s.CreatedAt, s.UpdatedAt, row.CompletedAt,
            SplitRef(SplitFromKind), SplitRef(SplitIntoKind));
    }

    public async Task<IssueResult<IssuePage>> ListAsync(IssueQuery query, CancellationToken ct)
    {
        var parsed = IssueSearch.Parse(query.Q);
        if (parsed.Error is { } invalid)
        {
            return invalid;
        }

        var search = parsed.Value!;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Parameters and q filters both apply (AND).
        var filtered = Filter(db, db.Issues.AsNoTracking(), query);
        if (filtered.Error is null)
        {
            filtered = Filter(db, filtered.Value!, search.Filters);
        }

        if (filtered.Error is { } error)
        {
            return error;
        }

        var issues = filtered.Value!;
        var sort = query.Sort ?? (search.Text is null ? IssueSort.Updated : IssueSort.Relevance);
        if (sort == IssueSort.Relevance && search.Text is null)
        {
            sort = IssueSort.Updated;
        }

        Cursor? cursor = null;
        if (query.Cursor is { } raw && (cursor = Cursor.TryParse(raw, sort)) is null)
        {
            return new IssueError("invalid_cursor", "The cursor is malformed or from a different sort.");
        }

        if (search.Text is { } text)
        {
            // The tsquery is built inside each lambda: as a local it would be evaluated on the client and fail.
            var key = text.Trim().ToUpperInvariant();
            issues = issues.Where(i => EF.Property<NpgsqlTsVector>(i, IssueConfiguration.SearchVector)
                    .Matches(EF.Functions.WebSearchToTsQuery("english", text).Or(EF.Functions.WebSearchToTsQuery("danish", text)))
                || KuproDbContext.WordSimilarity(text, i.Title) >= MinWordSimilarity
                || i.Key == key);

            if (sort == IssueSort.Relevance)
            {
                // Relevance is computed, so pages use an offset; deep paging through search results is rare.
                var offset = (int)Math.Clamp(cursor?.Number ?? 0, 0, int.MaxValue);
                var ranked = await issues
                    .OrderByDescending(i => (i.Key == key ? 2 : 0)
                        + EF.Property<NpgsqlTsVector>(i, IssueConfiguration.SearchVector)
                            .Rank(EF.Functions.WebSearchToTsQuery("english", text).Or(EF.Functions.WebSearchToTsQuery("danish", text)))
                        + KuproDbContext.WordSimilarity(text, i.Title))
                    .ThenByDescending(i => i.UpdatedAt).ThenByDescending(i => i.Id)
                    .Skip(offset).Take(query.Limit + 1).Select(SummaryOf(db)).ToListAsync(ct);
                return PageOf(ranked, query.Limit, _ => Cursor.ForOffset(offset + query.Limit));
            }
        }

        var page = await Paginate(issues, sort, cursor).Take(query.Limit + 1).Select(SummaryOf(db)).ToListAsync(ct);
        return PageOf(page, query.Limit, last => Cursor.For(last, sort));
    }

    /// <summary>Typo-tolerant title match threshold; pg_trgm's own default (0.6) misses one-letter typos in short words.</summary>
    private const float MinWordSimilarity = 0.5f;

    private static IssuePage PageOf(List<IssueSummary> rows, int limit, Func<IssueSummary, string> next)
    {
        if (rows.Count <= limit)
        {
            return new IssuePage(rows, null);
        }

        rows.RemoveAt(rows.Count - 1);
        return new IssuePage(rows, next(rows[^1]));
    }

    private IssueResult<IQueryable<Issue>> Filter(KuproDbContext db, IQueryable<Issue> issues, IssueQuery query)
    {
        if (query.Project is { Length: > 0 } projectKeys)
        {
            var keys = projectKeys.Select(k => k.ToUpperInvariant()).ToList();
            var projectIds = db.Projects.Where(p => keys.Contains(p.Key)).Select(p => p.Id);
            issues = issues.Where(i => projectIds.Contains(i.ProjectId));
        }

        if (query.State is { Length: > 0 } states)
        {
            issues = issues.Where(i => states.Contains(i.State));
        }

        if (query.Type is { Length: > 0 } types)
        {
            issues = issues.Where(i => types.Contains(i.Type));
        }

        if (query.Assignee is { } assignee)
        {
            if (assignee == Unassigned)
            {
                issues = issues.Where(i => i.AssigneeId == null);
            }
            else if (ResolveMemberShorthand(assignee) is { } memberId)
            {
                issues = issues.Where(i => i.AssigneeId == memberId);
            }
            else
            {
                return new IssueError("invalid_filter", "assignee must be a member id, 'me' or 'none'.");
            }
        }

        if (query.Label is { } label)
        {
            var name = LabelService.Normalize(label);
            issues = issues.Where(i => db.IssueLabels.Any(il => il.IssueId == i.Id
                && db.Labels.Any(l => l.Id == il.LabelId && l.Name == name)));
        }

        if (query.Sprint is { } sprint)
        {
            if (sprint == Unassigned)
            {
                issues = issues.Where(i => i.SprintId == null);
            }
            else if (sprint == ActiveSprint)
            {
                issues = issues.Where(i => db.Sprints.Any(s => s.Id == i.SprintId && s.State == SprintState.Active));
            }
            else if (Guid.TryParse(sprint, out var sprintId))
            {
                issues = issues.Where(i => i.SprintId == sprintId);
            }
            else
            {
                return new IssueError("invalid_filter", "sprint must be a sprint id, 'active' or 'none'.");
            }
        }

        if (query.Parent is { } parent)
        {
            var parentKey = NormalizeKey(parent);
            issues = issues.Where(i => db.Issues.Any(p => p.Id == i.ParentId && p.Key == parentKey));
        }

        return new IssueResult<IQueryable<Issue>>(issues, null);
    }

    // ---------------------------------------------------------------- create

    public async Task<IssueResult<IssueDetail>> CreateAsync(CreateIssueRequest request, CancellationToken ct)
    {
        var created = await CreateManyAsync([request], ct);
        return created.Error ?? (IssueResult<IssueDetail>)(await GetAsync(created.Value![0].Key, ct))!;
    }

    /// <summary>Creates issues all-or-nothing. Errors name the failing item as <c>issues[i]</c>.</summary>
    public async Task<IssueResult<IReadOnlyList<IssueSummary>>> CreateManyAsync(
        IReadOnlyList<CreateIssueRequest> requests, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var projectKeys = requests.Select(r => r.Project.ToUpperInvariant()).Distinct().ToList();
        var projects = await db.Projects.Where(p => projectKeys.Contains(p.Key)).ToDictionaryAsync(p => p.Key, p => p.Id, ct);
        var parentKeys = requests.Where(r => r.Parent is not null && !IsBatchRef(r.Parent)).Select(r => NormalizeKey(r.Parent!))
            .Distinct().ToList();
        var parents = await db.Issues.Where(i => parentKeys.Contains(i.Key))
            .ToDictionaryAsync(i => i.Key, i => new ParentInfo(i.Id, i.Type), ct);
        var assignees = await ResolveAssigneesAsync(db, requests.Select(r => r.Assignee), ct);

        var sprintIds = new Guid?[requests.Count];
        for (var n = 0; n < requests.Count; n++)
        {
            var r = requests[n];
            var where = requests.Count > 1 ? $"issues[{n}]: " : "";
            if (!projects.ContainsKey(r.Project.ToUpperInvariant()))
            {
                return new IssueError("project_not_found", $"{where}No project {r.Project}.");
            }

            if (r.Parent is not null && IsBatchRef(r.Parent))
            {
                if (!int.TryParse(r.Parent.AsSpan(1), out var index) || index < 0 || index >= n)
                {
                    return new IssueError("invalid_parent", $"{where}{r.Parent} must point to an earlier issue in this request.");
                }

                if (Validate.Parent(r.Type, new ParentInfo(Guid.Empty, requests[index].Type)) is { } batchError)
                {
                    return batchError with { Detail = where + batchError.Detail };
                }
            }
            else if (r.Parent is not null && Validate.Parent(r.Type, parents.GetValueOrDefault(NormalizeKey(r.Parent))) is { } parentError)
            {
                return parentError with { Detail = where + parentError.Detail };
            }

            if (r.Assignee is not null && AssigneeError(assignees, r.Assignee, where) is { } assigneeError)
            {
                return assigneeError;
            }

            if (r.StartDate > r.DueDate)
            {
                return new IssueError("invalid_dates", $"{where}Start date is after due date.");
            }

            if (r.Sprint is not null)
            {
                var (sprintId, sprintError) = await ResolveSprintAsync(db, r.Sprint, projects[r.Project.ToUpperInvariant()], ct);
                if (sprintError is not null)
                {
                    return sprintError with { Detail = where + sprintError.Detail };
                }

                sprintIds[n] = sprintId;
            }
        }

        // Ids are fixed before the strategy runs, so a commit whose acknowledgement was lost is recognised by
        // verifySucceeded instead of retried into duplicate issues with fresh numbers.
        var ids = requests.Select(_ => Guid.CreateVersion7()).ToList();
        // Retrying execution strategy: the transaction must run inside it as one retriable unit. The counter update locks
        // each project row (in id order, so two batches cannot deadlock) until commit: concurrent creates in a column then
        // read each other's top rank, and a failed save burns no issue numbers.
        await db.Database.CreateExecutionStrategy().ExecuteAsync<object?, bool>(null, async (_, _, _) =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var rows = new List<NewIssue>(requests.Count);
            foreach (var (r, index) in requests.Select((r, i) => (r, i)))
            {
                var projectKey = r.Project.ToUpperInvariant();
                rows.Add(new NewIssue(
                    ids[index], projects[projectKey], projectKey, r.Type, r.Title.Trim(), r.Description, r.State, r.Priority,
                    r.Assignee is null ? null : assignees[r.Assignee].Id,
                    r.Parent is null ? null
                        : IsBatchRef(r.Parent) ? ids[int.Parse(r.Parent.AsSpan(1), CultureInfo.InvariantCulture)]
                        : parents[NormalizeKey(r.Parent)].Id,
                    r.Effort, r.StartDate, r.DueDate, sprintIds[index],
                    await labels.ResolveAsync(db, r.Labels ?? [], ct)));
            }

            // Before the project counters in InsertAsync: closing a sprint takes the sprint first too.
            await SprintLedger.LockSprintsAsync(db, sprintIds.OfType<Guid>().Distinct().ToList(), ct);
            await InsertAsync(db, rows, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }, async (_, _, _) => new ExecutionResult<bool>(await db.Issues.AnyAsync(i => i.Id == ids[0], ct), true), ct);

        var summaries = await db.Issues.AsNoTracking().Where(i => ids.Contains(i.Id)).Select(SummaryOf(db)).ToListAsync(ct);
        var ordered = summaries.OrderBy(s => ids.IndexOf(s.Id)).ToList();
        await realtime.IssuesAsync(ordered, created: true);
        return ordered;
    }

    /// <summary>
    /// Adds issues to the caller's context and transaction: key, rank, activity and ledger rows. The counter update locks
    /// each project row until the caller commits.
    /// </summary>
    private async Task<List<Issue>> InsertAsync(KuproDbContext db, IReadOnlyList<NewIssue> rows, CancellationToken ct)
    {
        var numbers = new Dictionary<Guid, int>();
        foreach (var group in rows.GroupBy(r => r.ProjectId).OrderBy(g => g.Key))
        {
            var last = await ProjectService.AllocateIssueNumbersAsync(db, group.Key, group.Count(), ct);
            numbers[group.Key] = last - group.Count();
        }

        // New issues go on top of their column (project + state), in request order, so they are on its first page.
        var ranks = new Dictionary<(Guid, IssueState), double>();
        foreach (var group in rows.GroupBy(r => (r.ProjectId, r.State)))
        {
            var (projectId, state) = group.Key;
            var top = await db.Issues.Where(i => i.ProjectId == projectId && i.State == state).MinAsync(i => (double?)i.Rank, ct) ?? 0;
            ranks[group.Key] = top - group.Count() - 1;
        }

        var now = clock.GetUtcNow();
        var created = new List<Issue>(rows.Count);
        foreach (var r in rows)
        {
            var number = ++numbers[r.ProjectId];
            var issue = new Issue
            {
                Id = r.Id,
                WorkspaceId = workspace.RequireWorkspaceId(),
                ProjectId = r.ProjectId,
                Number = number,
                Key = $"{r.ProjectKey}-{number}",
                Type = r.Type,
                Title = r.Title,
                Description = r.Description,
                State = r.State,
                Priority = r.Priority,
                AssigneeId = r.AssigneeId,
                ParentId = r.ParentId,
                Effort = r.Effort,
                StartDate = r.StartDate,
                DueDate = r.DueDate,
                Rank = ++ranks[(r.ProjectId, r.State)],
                SprintId = r.SprintId,
                CreatedById = workspace.MemberId,
                CreatedAt = now,
                UpdatedAt = now,
                CompletedAt = IsClosed(r.State) ? now : null,
            };
            foreach (var labelId in r.LabelIds)
            {
                issue.Labels.Add(new IssueLabel { WorkspaceId = issue.WorkspaceId, IssueId = issue.Id, LabelId = labelId });
            }

            db.Issues.Add(issue);
            activity.Record(db, issue.Id, "created");
            created.Add(issue);
        }

        await ledger.RecordAsync(db, created.Select(i => (i.Id, LedgerState.None, LedgerState.Of(i))), ct);
        return created;
    }

    // ---------------------------------------------------------------- update

    public async Task<IssueResult<IssueDetail>> UpdateAsync(string key, UpdateIssueRequest request, CancellationToken ct)
    {
        var updated = await UpdateManyAsync([new IssueUpdate(key, request)], ct);
        return updated.Error ?? (IssueResult<IssueDetail>)(await GetAsync(updated.Value![0].Key, ct))!;
    }

    /// <summary>Applies updates all-or-nothing. Errors name the failing item as <c>items[i]</c>.</summary>
    public async Task<IssueResult<IReadOnlyList<IssueSummary>>> UpdateManyAsync(
        IReadOnlyList<IssueUpdate> updates, CancellationToken ct)
    {
        var applied = await InWriteAsync(db => ApplyUpdatesAsync(db, updates, ct), ct);
        if (applied.Error is { } error)
        {
            return error;
        }

        var ids = applied.Value!;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var summaries = await db.Issues.AsNoTracking().Where(i => ids.Contains(i.Id)).Select(SummaryOf(db)).ToListAsync(ct);
        var ordered = summaries.OrderBy(s => ids.IndexOf(s.Id)).ToList();
        await realtime.IssuesAsync(ordered);
        return ordered;
    }

    /// <summary>
    /// Runs a write in one transaction, committing unless it returns an error. A save that lost a race with another write
    /// to the same issue starts over on fresh data, so both changes land and the ledger follows what was actually stored.
    /// </summary>
    private async Task<IssueResult<T>> InWriteAsync<T>(Func<KuproDbContext, Task<IssueResult<T>>> write, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            try
            {
                IssueResult<T> result = default;
                await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    db.ChangeTracker.Clear();
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    result = await write(db);
                    if (result.Error is null)
                    {
                        await tx.CommitAsync(ct);
                    }
                });
                return result;
            }
            catch (DbUpdateConcurrencyException)
            {
                if (attempt == MaxWriteAttempts)
                {
                    return new IssueError("issue_conflict", "The issue is being changed by someone else right now. Try again.");
                }
            }
        }
    }

    /// <summary>Losing a race means another write got through, so a request only runs out with this many writers on one issue at once.</summary>
    private const int MaxWriteAttempts = 10;

    private async Task<IssueResult<List<Guid>>> ApplyUpdatesAsync(
        KuproDbContext db, IReadOnlyList<IssueUpdate> updates, CancellationToken ct)
    {
        var keys = updates.Select(u => NormalizeKey(u.Key)).Distinct().ToList();
        var issues = await db.Issues.Include(i => i.Labels).Where(i => keys.Contains(i.Key)).ToDictionaryAsync(i => i.Key, ct);
        var parentKeys = updates.Where(u => u.Changes.Parent is not null).Select(u => NormalizeKey(u.Changes.Parent!)).Distinct().ToList();
        var parents = await db.Issues.Where(i => parentKeys.Contains(i.Key))
            .ToDictionaryAsync(i => i.Key, i => new ParentInfo(i.Id, i.Type), ct);
        var assignees = await ResolveAssigneesAsync(db, updates.Select(u => u.Changes.Assignee), ct);
        var labelNames = await db.Labels.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);

        var before = issues.Values.Select(i => (Issue: i, State: LedgerState.Of(i))).ToList();
        var now = clock.GetUtcNow();
        for (var n = 0; n < updates.Count; n++)
        {
            var (key, c) = updates[n];
            var where = updates.Count > 1 ? $"items[{n}]: " : "";
            if (!issues.TryGetValue(NormalizeKey(key), out var issue))
            {
                var notFound = IssueError.NotFound(key);
                return notFound with { Detail = where + notFound.Detail };
            }

            var changes = new Dictionary<string, FieldChange>();
            var clear = c.Clear?.ToHashSet() ?? [];

            if (c.Title is { } title && title.Trim() != issue.Title)
            {
                changes["title"] = new(issue.Title, title.Trim());
                issue.Title = title.Trim();
            }

            if (c.Description is not null || clear.Contains(IssueField.Description))
            {
                var description = clear.Contains(IssueField.Description) ? null : c.Description;
                if (description != issue.Description)
                {
                    changes["description"] = new(null, null); // bodies are not copied into the log
                    issue.Description = description;
                }
            }

            if (c.Priority is { } priority && priority != issue.Priority)
            {
                changes["priority"] = new(issue.Priority, priority);
                issue.Priority = priority;
            }

            if (c.State is { } state && state != issue.State)
            {
                changes["state"] = new(issue.State, state);
                SetState(issue, state, now);
            }

            if (c.Effort is not null || clear.Contains(IssueField.Effort))
            {
                var effort = clear.Contains(IssueField.Effort) ? null : c.Effort;
                if (effort != issue.Effort)
                {
                    changes["effort"] = new(issue.Effort, effort);
                    issue.Effort = effort;
                }
            }

            if (c.StartDate is not null || clear.Contains(IssueField.StartDate))
            {
                var start = clear.Contains(IssueField.StartDate) ? null : c.StartDate;
                if (start != issue.StartDate)
                {
                    changes["startDate"] = new(issue.StartDate, start);
                    issue.StartDate = start;
                }
            }

            if (c.DueDate is not null || clear.Contains(IssueField.DueDate))
            {
                var due = clear.Contains(IssueField.DueDate) ? null : c.DueDate;
                if (due != issue.DueDate)
                {
                    changes["dueDate"] = new(issue.DueDate, due);
                    issue.DueDate = due;
                }
            }

            if (issue.StartDate > issue.DueDate)
            {
                return new IssueError("invalid_dates", $"{where}Start date is after due date.");
            }

            if (c.Assignee is not null || clear.Contains(IssueField.Assignee))
            {
                Guid? assigneeId = null;
                if (!clear.Contains(IssueField.Assignee) && AssigneeError(assignees, c.Assignee!, where) is { } assigneeError)
                {
                    return assigneeError;
                }
                else if (!clear.Contains(IssueField.Assignee))
                {
                    assigneeId = assignees[c.Assignee!].Id;
                }

                if (assigneeId != issue.AssigneeId)
                {
                    changes["assignee"] = new(issue.AssigneeId, assigneeId);
                    issue.AssigneeId = assigneeId;
                }
            }

            var newType = c.Type ?? issue.Type;
            Guid? newParentId = issue.ParentId;
            if (clear.Contains(IssueField.Parent))
            {
                newParentId = null;
            }
            else if (c.Parent is not null)
            {
                var parentKey = NormalizeKey(c.Parent);
                if (parentKey == issue.Key)
                {
                    return new IssueError("invalid_parent", $"{where}An issue can't be its own parent.");
                }

                if (Validate.Parent(newType, parents.GetValueOrDefault(parentKey)) is { } parentError)
                {
                    return parentError with { Detail = where + parentError.Detail };
                }

                newParentId = parents[parentKey].Id;
            }

            if (newType != issue.Type)
            {
                if (newParentId is { } pid && c.Parent is null)
                {
                    var parentType = await db.Issues.Where(p => p.Id == pid).Select(p => p.Type).SingleAsync(ct);
                    if (Validate.Parent(newType, new ParentInfo(pid, parentType)) is { } parentError)
                    {
                        return parentError with { Detail = where + parentError.Detail };
                    }
                }

                var childTypes = await db.Issues.Where(ch => ch.ParentId == issue.Id).Select(ch => ch.Type).Distinct().ToListAsync(ct);
                if (childTypes.Any(t => !Validate.CanBeParent(t, newType)))
                {
                    return new IssueError("invalid_hierarchy",
                        $"{where}{issue.Key} has children that can't sit under a {newType}.");
                }

                changes["type"] = new(issue.Type, newType);
                issue.Type = newType;
            }

            if (newParentId != issue.ParentId)
            {
                changes["parent"] = new(issue.ParentId, newParentId);
                issue.ParentId = newParentId;
            }

            if (c.Sprint is not null || clear.Contains(IssueField.Sprint))
            {
                Guid? sprintId = null;
                if (!clear.Contains(IssueField.Sprint))
                {
                    var (resolved, sprintError) = await ResolveSprintAsync(db, c.Sprint!, issue.ProjectId, ct);
                    if (sprintError is not null)
                    {
                        return sprintError with { Detail = where + sprintError.Detail };
                    }

                    sprintId = resolved;
                }

                if (sprintId != issue.SprintId)
                {
                    changes["sprint"] = new(issue.SprintId, sprintId);
                    issue.SprintId = sprintId;
                }
            }

            var labelsChanged = await ApplyLabelsAsync(db, issue, c, labelNames, ct);
            if (labelsChanged is not null)
            {
                changes["labels"] = labelsChanged;
            }

            if (changes.Count > 0)
            {
                issue.UpdatedAt = now;
                activity.Record(db, issue.Id, "updated", changes);
            }
        }

        await ledger.RecordAsync(db, before.Select(b => (b.Issue.Id, b.State, LedgerState.Of(b.Issue))), ct);
        await db.SaveChangesAsync(ct);
        return updates.Select(u => issues[NormalizeKey(u.Key)].Id).Distinct().ToList();
    }

    /// <summary>Board move: optional state change plus a position between two neighbours.</summary>
    public async Task<IssueResult<IssueSummary>> MoveAsync(string key, MoveIssueRequest request, CancellationToken ct)
    {
        var applied = await InWriteAsync(db => ApplyMoveAsync(db, key, request, ct), ct);
        if (applied.Error is { } error)
        {
            return error;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var moved = await db.Issues.AsNoTracking().Where(i => i.Id == applied.Value).Select(SummaryOf(db)).SingleAsync(ct);
        await realtime.IssuesAsync([moved]);
        return moved;
    }

    private async Task<IssueResult<Guid>> ApplyMoveAsync(KuproDbContext db, string key, MoveIssueRequest request, CancellationToken ct)
    {
        var issue = await db.Issues.SingleOrDefaultAsync(i => i.Key == NormalizeKey(key), ct);
        if (issue is null)
        {
            return IssueError.NotFound(key);
        }

        var neighbourKeys = new[] { request.After, request.Before }.OfType<string>().Select(NormalizeKey).ToList();
        var ranks = await db.Issues.Where(i => neighbourKeys.Contains(i.Key)).ToDictionaryAsync(i => i.Key, i => i.Rank, ct);
        double? after = request.After is null ? null : ranks.GetValueOrDefault(NormalizeKey(request.After), double.NaN);
        double? before = request.Before is null ? null : ranks.GetValueOrDefault(NormalizeKey(request.Before), double.NaN);
        if (after is double.NaN || before is double.NaN)
        {
            return new IssueError("issue_not_found", "A neighbour issue in after/before doesn't exist.");
        }

        var ledgerBefore = LedgerState.Of(issue);
        var now = clock.GetUtcNow();
        issue.Rank = (after, before) switch
        {
            ({ } a, { } b) => (a + b) / 2,
            ({ } a, null) => a + 1,
            (null, { } b) => b - 1,
            _ => issue.Rank,
        };

        if (request.State is { } state && state != issue.State)
        {
            activity.Record(db, issue.Id, "updated",
                new Dictionary<string, FieldChange> { ["state"] = new(issue.State, state) });
            SetState(issue, state, now);
        }

        issue.UpdatedAt = now;
        await ledger.RecordAsync(db, [(issue.Id, ledgerBefore, LedgerState.Of(issue))], ct);
        await db.SaveChangesAsync(ct);
        return issue.Id;
    }

    // ---------------------------------------------------------------- relations

    public async Task<IssueResult<IssueRelationResponse>> AddRelationAsync(
        string key, AddRelationRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var keys = new[] { NormalizeKey(key), NormalizeKey(request.Target) };
        var found = await db.Issues.Where(i => keys.Contains(i.Key))
            .ToDictionaryAsync(i => i.Key, i => new { i.Id, Ref = new IssueRef(i.Key, i.Title, i.Type, i.State) }, ct);
        if (!found.TryGetValue(keys[0], out var self))
        {
            return IssueError.NotFound(key);
        }

        if (!found.TryGetValue(keys[1], out var other))
        {
            return IssueError.NotFound(request.Target);
        }

        if (self.Id == other.Id)
        {
            return new IssueError("invalid_relation", "An issue can't relate to itself.");
        }

        var (source, target, type) = request.Type switch
        {
            RelationKind.Blocks => (self.Id, other.Id, RelationType.Blocks),
            RelationKind.BlockedBy => (other.Id, self.Id, RelationType.Blocks),
            RelationKind.Duplicates => (self.Id, other.Id, RelationType.Duplicates),
            RelationKind.DuplicatedBy => (other.Id, self.Id, RelationType.Duplicates),
            _ => (self.Id, other.Id, RelationType.Relates),
        };

        var exists = await db.IssueRelations.AnyAsync(r => r.Type == type
            && ((r.SourceId == source && r.TargetId == target)
                || (type == RelationType.Relates && r.SourceId == target && r.TargetId == source)), ct);
        if (exists)
        {
            return new IssueError("relation_exists", $"{keys[0]} already {request.Type} {keys[1]}.");
        }

        var relation = new IssueRelation
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.RequireWorkspaceId(),
            SourceId = source,
            TargetId = target,
            Type = type,
            CreatedAt = clock.GetUtcNow(),
        };
        db.IssueRelations.Add(relation);
        activity.Record(db, self.Id, "relation_added", new { type = request.Type, issue = keys[1] });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new IssueError("relation_exists", $"{keys[0]} already {request.Type} {keys[1]}.");
        }

        await realtime.ChangedAsync(ChangeTopic.Issue, keys[0]);
        await realtime.ChangedAsync(ChangeTopic.Issue, keys[1]);
        return new IssueRelationResponse(relation.Id, request.Type, other.Ref);
    }

    public async Task<IssueError?> RemoveRelationAsync(string key, Guid relationId, CancellationToken ct)
    {
        var normalized = NormalizeKey(key);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var issueId = await db.Issues.Where(i => i.Key == normalized).Select(i => (Guid?)i.Id).SingleOrDefaultAsync(ct);
        if (issueId is null)
        {
            return IssueError.NotFound(key);
        }

        var relation = await db.IssueRelations
            .Where(r => r.Id == relationId && (r.SourceId == issueId || r.TargetId == issueId))
            .Select(r => new { r.Id, r.Type, Other = db.Issues.Where(o => o.Id == (r.SourceId == issueId ? r.TargetId : r.SourceId)).Select(o => o.Key).First() })
            .SingleOrDefaultAsync(ct);
        if (relation is null)
        {
            return new IssueError("relation_not_found", $"{normalized} has no relation {relationId}.");
        }

        await db.IssueRelations.Where(r => r.Id == relationId).ExecuteDeleteAsync(ct);
        activity.Record(db, issueId.Value, "relation_removed", new { type = relation.Type, issue = relation.Other });
        await db.SaveChangesAsync(ct);
        await realtime.ChangedAsync(ChangeTopic.Issue, normalized);
        await realtime.ChangedAsync(ChangeTopic.Issue, relation.Other);
        return null;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Applies the close outcomes to a sprint's unfinished issues (ADR-020), in the caller's transaction and while the
    /// sprint is still active. Saves, but publishes nothing: the caller calls <see cref="PublishCloseAsync"/> after commit.
    /// </summary>
    internal async Task<IssueResult<AppliedClose>> ApplyCloseAsync(
        KuproDbContext db, Sprint sprint, CloseSprintRequest request, Guid? defaultTarget, IReadOnlyCollection<Guid> openSprints, CancellationToken ct)
    {
        var items = new Dictionary<string, CloseSprintItem>();
        foreach (var item in request.Items ?? [])
        {
            // MCP arguments don't pass the controller's validation; numbers also bind to enums.
            if (string.IsNullOrWhiteSpace(item?.Issue) || !Enum.IsDefined(item.Outcome))
            {
                return new IssueError("invalid_close_item", "Each item needs an issue key and an outcome: carry, split, backlog, cancel or done.");
            }

            if (!items.TryAdd(NormalizeKey(item.Issue), item))
            {
                return new IssueError("invalid_close_item", $"{item.Issue} is listed twice.");
            }
        }

        // Holds the rows, so an update already under way waits, then starts over and finds the issue moved.
        await db.Database.ExecuteSqlAsync(
            $"SELECT id FROM issues WHERE sprint_id = {sprint.Id} AND workspace_id = {db.CurrentWorkspaceId} ORDER BY id FOR UPDATE", ct);
        var unfinished = await db.Issues.Include(i => i.Labels)
            .Where(i => i.SprintId == sprint.Id && i.State != IssueState.Done && i.State != IssueState.Canceled)
            .OrderBy(i => i.Number)
            .ToListAsync(ct);
        var unfinishedKeys = unfinished.Select(i => i.Key).ToHashSet();
        var skipped = items.Keys.Where(k => !unfinishedKeys.Contains(k)).Order(StringComparer.Ordinal).ToList();

        var splitIds = unfinished.Where(i => items.GetValueOrDefault(i.Key)?.Outcome == CloseOutcome.Split).Select(i => (Guid?)i.Id).ToList();
        var parentsOfSplits = splitIds.Count == 0 ? [] : await db.Issues.Where(c => splitIds.Contains(c.ParentId))
            .Select(c => c.ParentId).Distinct().ToListAsync(ct);
        var defaultOutcome = request.MoveUnfinishedTo == UnfinishedDestination.Backlog ? CloseOutcome.Backlog : CloseOutcome.Carry;
        foreach (var issue in unfinished)
        {
            var item = items.GetValueOrDefault(issue.Key);
            var outcome = item?.Outcome ?? defaultOutcome;
            if (outcome is CloseOutcome.Carry or CloseOutcome.Split && (item?.Target ?? defaultTarget) is null)
            {
                return new IssueError("no_next_sprint", "There is no planned sprint to move unfinished issues to. Create one, or move them to the backlog.");
            }

            if (item is not null && CloseItemError(issue, item, parentsOfSplits.Contains(issue.Id), openSprints) is { } error)
            {
                return error;
            }
        }

        var projectKey = splitIds.Count == 0 ? "" : await db.Projects.Where(p => p.Id == sprint.ProjectId).Select(p => p.Key).SingleAsync(ct);
        var now = clock.GetUtcNow();
        var totals = new Dictionary<CloseOutcome, (int Count, decimal Hours)>();
        var moved = 0;
        var usedDefaultTarget = false;
        var continuations = new List<(Issue Original, NewIssue Continuation, decimal HoursDone, decimal HoursLeft)>();
        foreach (var issue in unfinished)
        {
            var item = items.GetValueOrDefault(issue.Key);
            var outcome = item?.Outcome ?? defaultOutcome;
            var before = LedgerState.Of(issue);
            var effort = issue.Effort ?? 0;
            var target = item?.Target ?? defaultTarget;
            usedDefaultTarget |= outcome is CloseOutcome.Carry or CloseOutcome.Split && item?.Target is null;
            Guid? to = sprint.Id;
            var hours = effort;
            switch (outcome)
            {
                case CloseOutcome.Carry:
                    to = target;
                    ledger.RecordLeaving(db, sprint.Id, issue, LedgerKind.Carried, effort);
                    issue.SprintId = to;
                    moved++;
                    break;
                case CloseOutcome.Backlog:
                    to = null;
                    ledger.RecordLeaving(db, sprint.Id, issue, LedgerKind.Backlogged, effort);
                    issue.SprintId = null;
                    moved++;
                    break;
                case CloseOutcome.Cancel:
                case CloseOutcome.Done:
                    var state = outcome == CloseOutcome.Done ? IssueState.Done : IssueState.Canceled;
                    activity.Record(db, issue.Id, "updated", new Dictionary<string, FieldChange> { ["state"] = new(issue.State, state) });
                    SetState(issue, state, now);
                    ledger.Record(db, issue.Id, before, LedgerState.Of(issue));
                    break;
                case CloseOutcome.Split:
                    var left = item!.HoursLeft!.Value;
                    var done = effort - left;
                    continuations.Add((issue, new NewIssue(
                        Guid.CreateVersion7(), issue.ProjectId, projectKey, issue.Type, issue.Title, issue.Description, issue.State,
                        issue.Priority, issue.AssigneeId, issue.ParentId, left, issue.StartDate, issue.DueDate, target,
                        issue.Labels.Select(l => l.LabelId).ToList()), done, left));
                    activity.Record(db, issue.Id, "updated", new Dictionary<string, FieldChange>
                    {
                        ["state"] = new(issue.State, IssueState.Done),
                        ["effort"] = new(effort, done),
                    });
                    // The hours done are a normal completion on close day; the hours left leave as the split row.
                    ledger.RecordLeaving(db, sprint.Id, issue, LedgerKind.Split, left);
                    issue.Effort = done;
                    SetState(issue, IssueState.Done, now);
                    ledger.Record(db, issue.Id, before with { Effort = done }, LedgerState.Of(issue));
                    hours = left;
                    moved++;
                    break;
            }

            issue.UpdatedAt = now;
            activity.Record(db, issue.Id, "sprint_closed", new { outcome, sprint = new FieldChange(sprint.Id, to) });
            var (count, sum) = totals.GetValueOrDefault(outcome);
            totals[outcome] = (count + 1, sum + hours);
        }

        // Saved before the continuations, so the next read sees these issues' new states.
        await db.SaveChangesAsync(ct);

        var created = await InsertAsync(db, continuations.Select(c => c.Continuation).ToList(), ct);
        foreach (var (original, continuation) in continuations.Select(c => c.Original).Zip(created))
        {
            db.IssueRelations.Add(new IssueRelation
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = original.WorkspaceId,
                SourceId = original.Id,
                TargetId = continuation.Id,
                Type = RelationType.Relates,
                CreatedAt = now,
            });
            activity.Record(db, original.Id, SplitIntoKind, new SplitLink(continuation.Key));
            activity.Record(db, continuation.Id, SplitFromKind, new SplitLink(original.Key));
        }

        var relationKeys = await MoveOpenBlocksAsync(db, continuations.Select(c => c.Original.Id).Zip(created.Select(c => c.Id))
            .ToDictionary(p => p.First, p => p.Second), ct);
        await db.SaveChangesAsync(ct);

        return new AppliedClose(
            totals.OrderBy(t => t.Key).Select(t => new CloseOutcomeTotal(t.Key, t.Value.Count, t.Value.Hours)).ToList(),
            moved,
            continuations.Zip(created, (c, n) => new SprintSplit(c.Original.Key, n.Key, c.HoursDone, c.HoursLeft)).ToList(),
            skipped,
            unfinished.Select(i => i.Id).ToList(),
            created.Select(i => i.Id).ToList(),
            relationKeys,
            usedDefaultTarget);
    }

    /// <summary>Pushes what a committed close changed.</summary>
    internal async Task PublishCloseAsync(AppliedClose applied, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var changed = applied.ChangedIds.Concat(applied.CreatedIds).ToList();
        var summaries = await db.Issues.AsNoTracking().Where(i => changed.Contains(i.Id)).Select(SummaryOf(db)).ToListAsync(ct);
        await realtime.IssuesAsync(summaries.Where(s => applied.ChangedIds.Contains(s.Id)).ToList());
        await realtime.IssuesAsync(summaries.Where(s => applied.CreatedIds.Contains(s.Id)).ToList(), created: true);
        foreach (var key in applied.RelationKeys)
        {
            await realtime.ChangedAsync(ChangeTopic.Issue, key);
        }
    }

    /// <summary>
    /// Open <c>blocks</c> relations follow the work to the continuation, since the Done original no longer blocks anything
    /// (ADR-020). Returns the keys of the other issues, whose relations changed.
    /// </summary>
    private static async Task<IReadOnlyList<string>> MoveOpenBlocksAsync(
        KuproDbContext db, Dictionary<Guid, Guid> continuationOf, CancellationToken ct)
    {
        if (continuationOf.Count == 0)
        {
            return [];
        }

        var splitIds = continuationOf.Keys.ToList();
        var blocks = await db.IssueRelations
            .Where(r => r.Type == RelationType.Blocks && (splitIds.Contains(r.SourceId) || splitIds.Contains(r.TargetId)))
            .ToListAsync(ct);
        var otherIds = blocks.SelectMany(r => new[] { r.SourceId, r.TargetId }).Where(id => !continuationOf.ContainsKey(id)).Distinct().ToList();
        var openOthers = await db.Issues.AsNoTracking()
            .Where(i => otherIds.Contains(i.Id) && i.State != IssueState.Done && i.State != IssueState.Canceled)
            .ToDictionaryAsync(i => i.Id, i => i.Key, ct);

        var touched = new HashSet<string>();
        foreach (var relation in blocks)
        {
            var sourceSplit = continuationOf.TryGetValue(relation.SourceId, out var newSource);
            var targetSplit = continuationOf.TryGetValue(relation.TargetId, out var newTarget);
            if (sourceSplit && targetSplit)
            {
                relation.SourceId = newSource;
                relation.TargetId = newTarget;
            }
            else if (sourceSplit && openOthers.TryGetValue(relation.TargetId, out var blocked))
            {
                relation.SourceId = newSource;
                touched.Add(blocked);
            }
            else if (targetSplit && openOthers.TryGetValue(relation.SourceId, out var blocker))
            {
                relation.TargetId = newTarget;
                touched.Add(blocker);
            }
        }

        return touched.ToList();
    }

    /// <summary>Rules for one listed item whose issue is still unfinished; items already finished are skipped, not checked.</summary>
    private static IssueError? CloseItemError(Issue issue, CloseSprintItem item, bool hasChildren, IReadOnlyCollection<Guid> openSprints)
    {
        if (item.Target is { } target && item.Outcome is CloseOutcome.Carry or CloseOutcome.Split && !openSprints.Contains(target))
        {
            return new IssueError("invalid_sprint", $"{issue.Key}: sprint {target} isn't an open sprint in this project, other than the one closing.");
        }

        if (item.Outcome != CloseOutcome.Split)
        {
            return item.HoursLeft is not null ? new IssueError("invalid_split", $"{issue.Key}: hoursLeft only applies to a split.")
                : item.Target is not null && item.Outcome != CloseOutcome.Carry
                    ? new IssueError("invalid_sprint", $"{issue.Key}: a target only applies to carry and split.")
                    : null;
        }

        if (issue.Effort is not { } effort)
        {
            return new IssueError("invalid_split", $"{issue.Key} has no effort to split.");
        }

        if (hasChildren)
        {
            return new IssueError("invalid_split", $"{issue.Key} has child issues and can't be split.");
        }

        // Effort is stored with two decimals; finer hours would round the two parts away from the original total.
        return item.HoursLeft is not { } left || left <= 0 || left >= effort || decimal.Round(left, 2) != left
            ? new IssueError("invalid_split", $"{issue.Key}: hoursLeft must be above 0 and below its effort ({effort}), in hundredths at most.")
            : null;
    }

    /// <summary>The other issue's key in a <c>split_from</c> or <c>split_into</c> entry.</summary>
    internal static string? SplitKey(string? data) =>
        data is null ? null : JsonSerializer.Deserialize<SplitLink>(data, JsonSerializerOptions.Web)?.Issue;

    /// <summary>Sprint id for <c>active</c> or an id; it must belong to the issue's project and not be closed.</summary>
    private static async Task<(Guid? Id, IssueError? Error)> ResolveSprintAsync(
        KuproDbContext db, string value, Guid projectId, CancellationToken ct)
    {
        var candidates = db.Sprints.Where(s => s.ProjectId == projectId && s.State != SprintState.Closed);
        var id = value == ActiveSprint
            ? await candidates.Where(s => s.State == SprintState.Active).Select(s => (Guid?)s.Id).SingleOrDefaultAsync(ct)
            : Guid.TryParse(value, out var parsed)
                ? await candidates.Where(s => s.Id == parsed).Select(s => (Guid?)s.Id).SingleOrDefaultAsync(ct)
                : null;
        return id is null
            ? (null, new IssueError("invalid_sprint", value == ActiveSprint
                ? "The project has no active sprint."
                : $"Sprint {value} isn't an open sprint in this issue's project."))
            : (id, null);
    }

    private static string NormalizeKey(string key) => key.Trim().ToUpperInvariant();

    /// <summary><c>#2</c> in a bulk create: the third issue of the same request.</summary>
    private static bool IsBatchRef(string parent) => parent.StartsWith('#');

    private static bool IsClosed(IssueState state) => state is IssueState.Done or IssueState.Canceled;

    private static void SetState(Issue issue, IssueState state, DateTimeOffset now)
    {
        issue.CompletedAt = IsClosed(state) ? issue.CompletedAt ?? now : null;
        issue.State = state;
    }

    private Guid? ResolveMemberShorthand(string value) =>
        value == Me ? workspace.MemberId : Guid.TryParse(value, out var id) ? id : null;

    /// <summary>Maps each assignee value (member id or <c>me</c>) to an active member; unknown values are left out.</summary>
    private async Task<Dictionary<string, (Guid Id, MemberKind Kind)>> ResolveAssigneesAsync(
        KuproDbContext db, IEnumerable<string?> values, CancellationToken ct)
    {
        var wanted = values.OfType<string>().Distinct()
            .Select(v => (Value: v, Id: ResolveMemberShorthand(v))).Where(x => x.Id is not null).ToList();
        var ids = wanted.Select(x => x.Id!.Value).Distinct().ToList();
        var active = await db.Members.Where(m => ids.Contains(m.Id) && m.Status == MemberStatus.Active)
            .ToDictionaryAsync(m => m.Id, m => m.Kind, ct);
        return wanted.Where(x => active.ContainsKey(x.Id!.Value))
            .ToDictionary(x => x.Value, x => (x.Id!.Value, active[x.Id!.Value]));
    }

    /// <summary>The assignee answers for the work, so it must be a person (ADR-008); agents act through activity instead.</summary>
    private static IssueError? AssigneeError(Dictionary<string, (Guid Id, MemberKind Kind)> assignees, string value, string where) =>
        !assignees.TryGetValue(value, out var member) ? new IssueError("assignee_not_found", $"{where}No active member {value}.")
        : member.Kind == MemberKind.Agent ? new IssueError("assignee_not_human", $"{where}Agents can't be assignees; assign the person responsible.")
        : null;

    /// <summary>Applies label replace/add/remove; returns the change for the activity log, or null if unchanged.</summary>
    private async Task<FieldChange?> ApplyLabelsAsync(
        KuproDbContext db, Issue issue, UpdateIssueRequest c, Dictionary<Guid, string> names, CancellationToken ct)
    {
        if (c.Labels is null && c.AddLabels is null && c.RemoveLabels is null)
        {
            return null;
        }

        var before = issue.Labels.Select(l => l.LabelId).ToHashSet();
        var after = c.Labels is null ? new HashSet<Guid>(before) : (await labels.ResolveAsync(db, c.Labels, ct)).ToHashSet();
        if (c.AddLabels is not null)
        {
            after.UnionWith(await labels.ResolveAsync(db, c.AddLabels, ct));
        }

        if (c.RemoveLabels is not null)
        {
            var remove = c.RemoveLabels.Select(LabelService.Normalize).ToHashSet();
            after.RemoveWhere(id => names.TryGetValue(id, out var n) && remove.Contains(n));
        }

        if (before.SetEquals(after))
        {
            return null;
        }

        issue.Labels.RemoveAll(l => !after.Contains(l.LabelId));
        foreach (var id in after.Except(before))
        {
            issue.Labels.Add(new IssueLabel { WorkspaceId = issue.WorkspaceId, IssueId = issue.Id, LabelId = id });
        }

        string Name(Guid id) => names.TryGetValue(id, out var n) ? n
            : db.ChangeTracker.Entries<Label>().First(e => e.Entity.Id == id).Entity.Name;
        return new FieldChange(before.Select(Name).Order().ToList(), after.Select(Name).Order().ToList());
    }

    private static RelationKind ViewOf(RelationType type, bool outgoing) => type switch
    {
        RelationType.Blocks => outgoing ? RelationKind.Blocks : RelationKind.BlockedBy,
        RelationType.Duplicates => outgoing ? RelationKind.Duplicates : RelationKind.DuplicatedBy,
        _ => RelationKind.Relates,
    };

    /// <summary>List row projection; one SQL query for a whole page (no N+1).</summary>
    private static Expression<Func<Issue, IssueSummary>> SummaryOf(KuproDbContext db) =>
        i => new IssueSummary(
            i.Id,
            i.Key,
            i.Type,
            i.Title,
            i.State,
            i.Priority,
            db.Members.Where(m => m.Id == i.AssigneeId)
                .Select(m => new MemberRef(
                    m.Id,
                    m.Kind == MemberKind.Agent
                        ? m.AgentName!
                        : db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefault()!,
                    m.Kind))
                .FirstOrDefault(),
            db.Issues.Where(p => p.Id == i.ParentId).Select(p => p.Key).FirstOrDefault(),
            i.Effort,
            i.StartDate,
            i.DueDate,
            db.IssueLabels.Where(il => il.IssueId == i.Id)
                .Join(db.Labels, il => il.LabelId, l => l.Id, (il, l) => l.Name)
                .OrderBy(n => n)
                .ToList(),
            i.Rank,
            i.SprintId,
            i.CreatedAt,
            i.UpdatedAt);

    private static IQueryable<Issue> Paginate(IQueryable<Issue> issues, IssueSort sort, Cursor? c) => sort switch
    {
        // Row-value comparisons ((a, b) < (x, y)) keep keyset pagination on one index range.
        IssueSort.Created => (c is null ? issues : issues.Where(i =>
                EF.Functions.LessThan(ValueTuple.Create(i.CreatedAt, i.Id), ValueTuple.Create(c.Time, c.Id))))
            .OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id),
        IssueSort.Priority => (c is null ? issues : issues.Where(i =>
                EF.Functions.LessThan(ValueTuple.Create(i.Priority, i.Id), ValueTuple.Create((IssuePriority)c.Number, c.Id))))
            .OrderByDescending(i => i.Priority).ThenByDescending(i => i.Id),
        IssueSort.Rank => (c is null ? issues : issues.Where(i =>
                EF.Functions.GreaterThan(ValueTuple.Create(i.Rank, i.Id), ValueTuple.Create(c.Number, c.Id))))
            .OrderBy(i => i.Rank).ThenBy(i => i.Id),
        _ => (c is null ? issues : issues.Where(i =>
                EF.Functions.LessThan(ValueTuple.Create(i.UpdatedAt, i.Id), ValueTuple.Create(c.Time, c.Id))))
            .OrderByDescending(i => i.UpdatedAt).ThenByDescending(i => i.Id),
    };

    private sealed record ParentInfo(Guid Id, IssueType Type);

    private sealed record SplitLink(string Issue);

    /// <summary>An issue about to be inserted, with everything already resolved and validated.</summary>
    private sealed record NewIssue(
        Guid Id,
        Guid ProjectId,
        string ProjectKey,
        IssueType Type,
        string Title,
        string? Description,
        IssueState State,
        IssuePriority Priority,
        Guid? AssigneeId,
        Guid? ParentId,
        decimal? Effort,
        DateOnly? StartDate,
        DateOnly? DueDate,
        Guid? SprintId,
        IReadOnlyList<Guid> LabelIds);

    private static class Validate
    {
        public static bool CanBeParent(IssueType child, IssueType parent) => child switch
        {
            IssueType.Task => parent is IssueType.Feature or IssueType.Epic,
            IssueType.Feature => parent is IssueType.Epic,
            _ => false,
        };

        public static IssueError? Parent(IssueType child, ParentInfo? parent) =>
            parent is null ? new IssueError("invalid_parent", "The parent issue doesn't exist.")
            : CanBeParent(child, parent.Type) ? null
            : new IssueError("invalid_hierarchy", child == IssueType.Epic
                ? "Epics are top level and can't have a parent."
                : $"A {child} can't sit under a {parent.Type}.");
    }

    /// <summary>Opaque keyset cursor: sort, last row's sort value and id.</summary>
    private sealed record Cursor(IssueSort Sort, DateTimeOffset Time, double Number, Guid Id)
    {
        public static string For(IssueSummary last, IssueSort sort) => Encode(sort switch
        {
            IssueSort.Created => $"{sort}|{last.Id}|{last.CreatedAt.ToString("O", CultureInfo.InvariantCulture)}",
            IssueSort.Priority => $"{sort}|{last.Id}|{(int)last.Priority}",
            IssueSort.Rank => $"{sort}|{last.Id}|{last.Rank.ToString("R", CultureInfo.InvariantCulture)}",
            _ => $"{sort}|{last.Id}|{last.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)}",
        });

        public static string ForOffset(int offset) =>
            Encode($"{IssueSort.Relevance}|{Guid.Empty}|{offset.ToString(CultureInfo.InvariantCulture)}");

        public static Cursor? TryParse(string raw, IssueSort expected)
        {
            try
            {
                var parts = Encoding.UTF8.GetString(Convert.FromBase64String(raw.Replace('-', '+').Replace('_', '/')
                    .PadRight(raw.Length + ((4 - (raw.Length % 4)) % 4), '='))).Split('|');
                if (parts.Length != 3 || !Enum.TryParse<IssueSort>(parts[0], out var sort) || sort != expected
                    || !Guid.TryParse(parts[1], out var id))
                {
                    return null;
                }

                return sort is IssueSort.Updated or IssueSort.Created
                    ? DateTimeOffset.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                        ? new Cursor(sort, t, 0, id) : null
                    : double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                        ? new Cursor(sort, default, d, id) : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static string Encode(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

/// <summary>What <see cref="IssueService.ApplyCloseAsync"/> did; the ids and keys are for pushing it after commit.</summary>
internal sealed record AppliedClose(
    IReadOnlyList<CloseOutcomeTotal> Outcomes,
    int Moved,
    IReadOnlyList<SprintSplit> Splits,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<Guid> ChangedIds,
    IReadOnlyList<Guid> CreatedIds,
    IReadOnlyList<string> RelationKeys,
    bool UsedDefaultTarget);
