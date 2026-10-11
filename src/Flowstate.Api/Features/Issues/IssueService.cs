using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using Flowstate.Api.Data;
using Flowstate.Api.Features.Activity;
using Flowstate.Api.Features.Projects;
using Flowstate.Api.Features.Workspaces;
using Flowstate.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Flowstate.Api.Features.Issues;

/// <summary>
/// Issues in the current workspace (task 004, ADR-008): create, read, list, update, move and relations. Owns Issue,
/// IssueLabel and IssueRelation, and records an activity entry for every change.
/// </summary>
public sealed class IssueService(
    IDbContextFactory<FlowstateDbContext> dbFactory,
    WorkspaceContext workspace,
    LabelService labels,
    ActivityLog activity,
    TimeProvider clock)
{
    public const string Me = "me";
    public const string Unassigned = "none";

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
            })
            .SingleAsync(ct);

        var relations = row.Outgoing.Select(r => new IssueRelationResponse(r.Id, ViewOf(r.Type, outgoing: true), r.Other))
            .Concat(row.Incoming.Select(r => new IssueRelationResponse(r.Id, ViewOf(r.Type, outgoing: false), r.Other)))
            .OrderBy(r => r.Type).ThenBy(r => r.Issue.Key, StringComparer.Ordinal)
            .ToList();
        return new IssueDetail(s.Id, s.Key, row.ProjectKey, s.Type, s.Title, row.Description, s.State, s.Priority,
            s.Assignee, row.Parent, s.Effort, row.Children.Count > 0 ? row.ChildEffort : null, s.StartDate, s.DueDate,
            s.Labels, row.Children, relations, s.Rank, row.CreatedBy, s.CreatedAt, s.UpdatedAt, row.CompletedAt);
    }

    public async Task<IssueResult<IssuePage>> ListAsync(IssueQuery query, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var issues = db.Issues.AsNoTracking();

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

        if (query.Parent is { } parent)
        {
            var parentKey = NormalizeKey(parent);
            issues = issues.Where(i => db.Issues.Any(p => p.Id == i.ParentId && p.Key == parentKey));
        }

        Cursor? cursor = null;
        if (query.Cursor is { } raw && (cursor = Cursor.TryParse(raw, query.Sort)) is null)
        {
            return new IssueError("invalid_cursor", "The cursor is malformed or from a different sort.");
        }

        issues = Paginate(issues, query.Sort, cursor);
        var page = await issues.Take(query.Limit + 1).Select(SummaryOf(db)).ToListAsync(ct);

        string? next = null;
        if (page.Count > query.Limit)
        {
            page.RemoveAt(page.Count - 1);
            next = Cursor.For(page[^1], query.Sort);
        }

        return new IssuePage(page, next);
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
        var parentKeys = requests.Where(r => r.Parent is not null).Select(r => NormalizeKey(r.Parent!)).Distinct().ToList();
        var parents = await db.Issues.Where(i => parentKeys.Contains(i.Key))
            .ToDictionaryAsync(i => i.Key, i => new ParentInfo(i.Id, i.Type), ct);
        var assignees = await ResolveAssigneesAsync(db, requests.Select(r => r.Assignee), ct);

        for (var n = 0; n < requests.Count; n++)
        {
            var r = requests[n];
            var where = requests.Count > 1 ? $"issues[{n}]: " : "";
            if (!projects.ContainsKey(r.Project.ToUpperInvariant()))
            {
                return new IssueError("project_not_found", $"{where}No project {r.Project}.");
            }

            if (r.Parent is not null && Validate.Parent(r.Type, parents.GetValueOrDefault(NormalizeKey(r.Parent))) is { } parentError)
            {
                return parentError with { Detail = where + parentError.Detail };
            }

            if (r.Assignee is not null && !assignees.ContainsKey(r.Assignee))
            {
                return new IssueError("assignee_not_found", $"{where}No active member {r.Assignee}.");
            }

            if (r.StartDate > r.DueDate)
            {
                return new IssueError("invalid_dates", $"{where}Start date is after due date.");
            }
        }

        var numbers = new Dictionary<Guid, int>();
        foreach (var group in requests.GroupBy(r => projects[r.Project.ToUpperInvariant()]))
        {
            var last = await ProjectService.AllocateIssueNumbersAsync(db, group.Key, group.Count(), ct);
            numbers[group.Key] = last - group.Count();
        }

        var now = clock.GetUtcNow();
        var created = new List<Issue>(requests.Count);
        foreach (var r in requests)
        {
            var projectKey = r.Project.ToUpperInvariant();
            var projectId = projects[projectKey];
            var number = ++numbers[projectId];
            var issue = new Issue
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = workspace.RequireWorkspaceId(),
                ProjectId = projectId,
                Number = number,
                Key = $"{projectKey}-{number}",
                Type = r.Type,
                Title = r.Title.Trim(),
                Description = r.Description,
                State = r.State,
                Priority = r.Priority,
                AssigneeId = r.Assignee is null ? null : assignees[r.Assignee],
                ParentId = r.Parent is null ? null : parents[NormalizeKey(r.Parent)].Id,
                Effort = r.Effort,
                StartDate = r.StartDate,
                DueDate = r.DueDate,
                // New issues go to the bottom of their column; the project counter is monotonic.
                Rank = number,
                CreatedById = workspace.MemberId,
                CreatedAt = now,
                UpdatedAt = now,
                CompletedAt = IsClosed(r.State) ? now : null,
            };
            foreach (var labelId in await labels.ResolveAsync(db, r.Labels ?? [], ct))
            {
                issue.Labels.Add(new IssueLabel { WorkspaceId = issue.WorkspaceId, IssueId = issue.Id, LabelId = labelId });
            }

            db.Issues.Add(issue);
            activity.Record(db, issue.Id, "created");
            created.Add(issue);
        }

        await db.SaveChangesAsync(ct);

        var ids = created.Select(i => i.Id).ToList();
        var summaries = await db.Issues.AsNoTracking().Where(i => ids.Contains(i.Id)).Select(SummaryOf(db)).ToListAsync(ct);
        return summaries.OrderBy(s => ids.IndexOf(s.Id)).ToList();
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
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var keys = updates.Select(u => NormalizeKey(u.Key)).Distinct().ToList();
        var issues = await db.Issues.Include(i => i.Labels).Where(i => keys.Contains(i.Key)).ToDictionaryAsync(i => i.Key, ct);
        var parentKeys = updates.Where(u => u.Changes.Parent is not null).Select(u => NormalizeKey(u.Changes.Parent!)).Distinct().ToList();
        var parents = await db.Issues.Where(i => parentKeys.Contains(i.Key))
            .ToDictionaryAsync(i => i.Key, i => new ParentInfo(i.Id, i.Type), ct);
        var assignees = await ResolveAssigneesAsync(db, updates.Select(u => u.Changes.Assignee), ct);
        var labelNames = await db.Labels.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);

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
                if (!clear.Contains(IssueField.Assignee) && !assignees.TryGetValue(c.Assignee!, out var resolved))
                {
                    return new IssueError("assignee_not_found", $"{where}No active member {c.Assignee}.");
                }
                else if (!clear.Contains(IssueField.Assignee))
                {
                    assigneeId = assignees[c.Assignee!];
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

        await db.SaveChangesAsync(ct);

        var ids = updates.Select(u => issues[NormalizeKey(u.Key)].Id).Distinct().ToList();
        var summaries = await db.Issues.AsNoTracking().Where(i => ids.Contains(i.Id)).Select(SummaryOf(db)).ToListAsync(ct);
        return summaries.OrderBy(s => ids.IndexOf(s.Id)).ToList();
    }

    /// <summary>Board move: optional state change plus a position between two neighbours.</summary>
    public async Task<IssueResult<IssueSummary>> MoveAsync(string key, MoveIssueRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
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
        await db.SaveChangesAsync(ct);
        return await db.Issues.AsNoTracking().Where(i => i.Id == issue.Id).Select(SummaryOf(db)).SingleAsync(ct);
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
        return null;
    }

    // ---------------------------------------------------------------- helpers

    private static string NormalizeKey(string key) => key.Trim().ToUpperInvariant();

    private static bool IsClosed(IssueState state) => state is IssueState.Done or IssueState.Canceled;

    private static void SetState(Issue issue, IssueState state, DateTimeOffset now)
    {
        issue.CompletedAt = IsClosed(state) ? issue.CompletedAt ?? now : null;
        issue.State = state;
    }

    private Guid? ResolveMemberShorthand(string value) =>
        value == Me ? workspace.MemberId : Guid.TryParse(value, out var id) ? id : null;

    /// <summary>Maps each assignee value (member id or <c>me</c>) to an active member id; unknown values are left out.</summary>
    private async Task<Dictionary<string, Guid>> ResolveAssigneesAsync(
        FlowstateDbContext db, IEnumerable<string?> values, CancellationToken ct)
    {
        var wanted = values.OfType<string>().Distinct()
            .Select(v => (Value: v, Id: ResolveMemberShorthand(v))).Where(x => x.Id is not null).ToList();
        var ids = wanted.Select(x => x.Id!.Value).Distinct().ToList();
        var active = await db.Members.Where(m => ids.Contains(m.Id) && m.Status == MemberStatus.Active)
            .Select(m => m.Id).ToListAsync(ct);
        return wanted.Where(x => active.Contains(x.Id!.Value)).ToDictionary(x => x.Value, x => x.Id!.Value);
    }

    /// <summary>Applies label replace/add/remove; returns the change for the activity log, or null if unchanged.</summary>
    private async Task<FieldChange?> ApplyLabelsAsync(
        FlowstateDbContext db, Issue issue, UpdateIssueRequest c, Dictionary<Guid, string> names, CancellationToken ct)
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
    private static Expression<Func<Issue, IssueSummary>> SummaryOf(FlowstateDbContext db) =>
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
