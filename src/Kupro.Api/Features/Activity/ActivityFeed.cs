using System.Globalization;
using System.Text.Json;
using Kupro.Api.Data;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Api.Features.Activity;

public enum FeedItemKind
{
    Comment,
    Created,
    Updated,
    RelationAdded,
    RelationRemoved,
    SprintClosed,
    SplitFrom,
    SplitInto,

    /// <summary>The issue came across in an import (ADR-019 History).</summary>
    Imported,

    /// <summary>A re-run of the import changed fields Kupro has; <see cref="FeedItem.Changes"/> as for <see cref="Updated"/>.</summary>
    Reimported,

    /// <summary>A source change to a field Kupro lacks: the field and values as the source named them.</summary>
    SourceChanged,
}

/// <summary>
/// One field change. Values are display-ready strings: enum names (<c>inProgress</c>), member names, issue keys,
/// sprint names, ISO dates, comma-separated labels. Description changes carry no values.
/// For relations, <c>Field</c> is the relation type and <c>To</c> (added) or <c>From</c> (removed) the other issue.
/// A sprint close has <c>outcome</c> (<c>To</c> is the outcome) and <c>sprint</c>; a split has <c>issue</c> (<c>To</c> is the other key).
/// </summary>
public sealed record FeedChange(string Field, string? From, string? To);

/// <summary>The meeting a change came from (task 015).</summary>
/// <param name="Snippet">Transcript words that triggered it; null once the transcript is deleted.</param>
/// <param name="ApprovedBy">Who approved it; null when full auto applied it.</param>
public sealed record FeedMeeting(Guid MeetingId, Guid ProposalId, string? Snippet, MemberRef? ApprovedBy);

/// <param name="Body">Comment markdown; null for other kinds.</param>
/// <param name="EditedAt">When a comment was last edited.</param>
/// <param name="Meeting">Set when the meeting agent made this change.</param>
/// <param name="SourceActor">For an import: the source's name for someone the owner skipped, whose entries the shared placeholder holds.</param>
/// <param name="Source">For an import: the source kind it came from (<c>jira</c>, <c>azure-devops</c>...), for "via Jira import".</param>
public sealed record FeedItem(
    Guid Id,
    FeedItemKind Kind,
    MemberRef Actor,
    Surface Via,
    DateTimeOffset At,
    IReadOnlyList<FeedChange> Changes,
    string? Body,
    DateTimeOffset? EditedAt,
    FeedMeeting? Meeting = null,
    string? SourceActor = null,
    string? Source = null);

/// <summary>An issue's history and comments merged in time order (task 008).</summary>
public sealed class ActivityFeed(IDbContextFactory<KuproDbContext> dbFactory)
{
    /// <summary>Most items in one response; older ones come a page at a time (ADR-010). An import can leave thousands.</summary>
    public const int Limit = 500;

    /// <summary>
    /// A page of an issue's feed, oldest first: the <paramref name="limit"/> newest items, or with <paramref name="before"/>
    /// (the id of the oldest item the caller has) the ones just before it. A full page means there may be more.
    /// Null if there is no such issue.
    /// </summary>
    public async Task<IssueResult<IReadOnlyList<FeedItem>>?> GetAsync(string key, Guid? before, int limit, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if ((await IssueKeys.ResolveAsync(db, [key], ct)).Values.SingleOrDefault() is not { } found)
        {
            return null;
        }

        var issueId = await db.Issues.Where(i => i.Key == found).Select(i => i.Id).SingleAsync(ct);
        (DateTimeOffset At, Guid Id)? cursor = null;
        if (before is { } id)
        {
            var at = await db.Activity.Where(a => a.Id == id && a.IssueId == issueId).Select(a => (DateTimeOffset?)a.CreatedAt).SingleOrDefaultAsync(ct)
                ?? await db.Comments.Where(c => c.Id == id && c.IssueId == issueId).Select(c => (DateTimeOffset?)c.CreatedAt).SingleOrDefaultAsync(ct);
            if (at is null)
            {
                return new IssueError("invalid_cursor", $"{id} is not an item in {found}'s activity.");
            }

            cursor = (at.Value, id);
        }

        var feeds = await LoadAsync(db, [(issueId, found)], cursor, Math.Clamp(limit, 1, Limit), ct);
        return new IssueResult<IReadOnlyList<FeedItem>>(feeds[found], null);
    }

    /// <summary>The newest page of each issue's feed by issue key, for the issues that exist among <paramref name="keys"/>. The same queries however many keys.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<FeedItem>>> GetManyAsync(IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var normalized = (await IssueKeys.ResolveAsync(db, keys, ct)).Values.Distinct().ToList();
        var issues = await db.Issues.Where(i => normalized.Contains(i.Key)).Select(i => new { i.Id, i.Key }).ToListAsync(ct);
        return await LoadAsync(db, [.. issues.Select(i => (i.Id, i.Key))], null, Limit, ct);
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<FeedItem>>> LoadAsync(
        KuproDbContext db, IReadOnlyList<(Guid Id, string Key)> issues, (DateTimeOffset At, Guid Id)? cursor, int limit, CancellationToken ct)
    {
        if (issues.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<FeedItem>>();
        }

        // Each side takes a full page and the merge below keeps the newest of both. Row values keep each read on one index range.
        var issueIds = issues.Select(i => i.Id).ToList();
        var paged = cursor is not null;
        var (beforeAt, beforeId) = cursor ?? default;
        var entries = await db.Issues.Where(i => issueIds.Contains(i.Id))
            .SelectMany(i => db.Activity
                .Where(a => a.IssueId == i.Id && (!paged || EF.Functions.LessThan(ValueTuple.Create(a.CreatedAt, a.Id), ValueTuple.Create(beforeAt, beforeId))))
                .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Take(limit))
            .Select(a => new { a.Id, a.IssueId, a.Kind, a.ActorId, a.Via, a.Data, a.CreatedAt, a.ProposalId })
            .ToListAsync(ct);
        var comments = await db.Issues.Where(i => issueIds.Contains(i.Id))
            .SelectMany(i => db.Comments
                .Where(c => c.IssueId == i.Id && (!paged || EF.Functions.LessThan(ValueTuple.Create(c.CreatedAt, c.Id), ValueTuple.Create(beforeAt, beforeId))))
                .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Take(limit))
            .Select(c => new { c.Id, c.IssueId, c.AuthorId, c.Via, c.Body, c.CreatedAt, c.EditedAt, c.ProposalId, c.SourceAuthor })
            .ToListAsync(ct);

        // An issue comes from at most one import; entries made through it say which source.
        var sources = await db.ImportedItems.AsNoTracking().Where(r => r.SourceKind == "item" && issueIds.Contains(r.EntityId))
            .Join(db.WorkspaceImports, r => r.ImportId, w => w.Id, (r, w) => new { r.EntityId, w.SourceKind })
            .ToDictionaryAsync(r => r.EntityId, r => r.SourceKind, ct);
        string? SourceOf(Guid issueId, Surface via) => via == Surface.Import ? sources.GetValueOrDefault(issueId) : null;

        var parsed = entries.Select(e => (Entry: e, Data: Parse(e.Data))).ToList();
        var proposalIds = entries.Select(e => e.ProposalId).Concat(comments.Select(c => c.ProposalId))
            .Where(p => p is not null).Select(p => p!.Value).Distinct().ToList();
        var proposals = await db.MeetingProposals.AsNoTracking().Where(p => proposalIds.Contains(p.Id))
            .Select(p => new { p.Id, p.SessionId, p.Snippet, p.ReviewedById }).ToDictionaryAsync(p => p.Id, ct);
        var memberIds = parsed.SelectMany(p => Ids(p.Data, "assignee")).Concat(entries.Select(e => e.ActorId))
            .Concat(comments.Select(c => c.AuthorId))
            .Concat(proposals.Values.Where(p => p.ReviewedById is not null).Select(p => p.ReviewedById!.Value)).ToHashSet();
        var members = await db.Members.Where(m => memberIds.Contains(m.Id))
            .Select(m => new MemberRef(
                m.Id,
                m.Kind == MemberKind.Agent
                    ? m.AgentName!
                    : m.DisplayName ?? db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefault()!,
                m.Kind))
            .ToDictionaryAsync(m => m.Id, ct);
        var parentIds = parsed.SelectMany(p => Ids(p.Data, "parent")).ToHashSet();
        var issueKeys = await db.Issues.Where(i => parentIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Key, ct);
        var sprintIds = parsed.SelectMany(p => Ids(p.Data, "sprint")).ToHashSet();
        var sprints = await db.Sprints.Where(s => sprintIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        string? Display(string field, JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(v => v.ToString())),
            JsonValueKind.Number => field switch
            {
                // Entries written before enums were stored as names.
                "state" => Name((IssueState)value.GetInt32()),
                "priority" => Name((IssuePriority)value.GetInt32()),
                "type" => Name((IssueType)value.GetInt32()),
                _ => value.GetDecimal().ToString(CultureInfo.InvariantCulture),
            },
            JsonValueKind.String when Guid.TryParse(value.GetString(), out var id) => field switch
            {
                "assignee" => members.TryGetValue(id, out var m) ? m.Name : null,
                "parent" => issueKeys.GetValueOrDefault(id),
                "sprint" => sprints.GetValueOrDefault(id),
                _ => value.GetString(),
            },
            _ => value.ToString(),
        };

        FeedMeeting? Meeting(Guid? proposalId) =>
            proposalId is { } id && proposals.TryGetValue(id, out var p)
                ? new FeedMeeting(p.SessionId, p.Id, p.Snippet, p.ReviewedById is { } r ? members.GetValueOrDefault(r) : null)
                : null;

        var items = new List<(Guid IssueId, FeedItem Item)>(parsed.Count + comments.Count);
        foreach (var (e, data) in parsed)
        {
            if (Kind(e.Kind) is not { } kind || !members.TryGetValue(e.ActorId, out var actor))
            {
                continue;
            }

            var changes = new List<FeedChange>();
            if (kind is FeedItemKind.Updated or FeedItemKind.Reimported && data is { ValueKind: JsonValueKind.Object } fields)
            {
                // Only field changes: an import also keeps the source's name for a skipped actor here (ADR-019 People).
                foreach (var f in fields.EnumerateObject().Where(f => f.Value.ValueKind == JsonValueKind.Object))
                {
                    changes.Add(new FeedChange(f.Name,
                        f.Value.TryGetProperty("from", out var from) ? Display(f.Name, from) : null,
                        f.Value.TryGetProperty("to", out var to) ? Display(f.Name, to) : null));
                }
            }
            else if (kind is FeedItemKind.SprintClosed && data is { ValueKind: JsonValueKind.Object } close)
            {
                changes.Add(new FeedChange("outcome", null, close.GetProperty("outcome").GetString()));
                var sprint = close.GetProperty("sprint");
                changes.Add(new FeedChange("sprint", Display("sprint", sprint.GetProperty("from")), Display("sprint", sprint.GetProperty("to"))));
            }
            else if (kind is FeedItemKind.SourceChanged && data is { ValueKind: JsonValueKind.Object } raw)
            {
                changes.Add(new FeedChange(Text(raw, "field") ?? "", Text(raw, "from"), Text(raw, "to")));
            }
            else if (kind is FeedItemKind.SplitFrom or FeedItemKind.SplitInto && data is { ValueKind: JsonValueKind.Object } split)
            {
                changes.Add(new FeedChange("issue", null, split.GetProperty("issue").GetString()));
            }
            else if (kind is FeedItemKind.RelationAdded or FeedItemKind.RelationRemoved && data is { ValueKind: JsonValueKind.Object } relation)
            {
                var type = relation.GetProperty("type");
                var typeName = type.ValueKind == JsonValueKind.Number
                    ? kind == FeedItemKind.RelationAdded ? Name((RelationKind)type.GetInt32()) : Name((RelationType)type.GetInt32())
                    : type.GetString()!;
                var other = relation.GetProperty("issue").GetString();
                changes.Add(kind == FeedItemKind.RelationAdded ? new(typeName, null, other) : new(typeName, other, null));
            }

            var sourceActor = data is { ValueKind: JsonValueKind.Object } d ? Text(d, "sourceActor") : null;
            items.Add((e.IssueId, new FeedItem(e.Id, kind, actor, e.Via, e.CreatedAt, changes, null, null, Meeting(e.ProposalId), sourceActor, SourceOf(e.IssueId, e.Via))));
        }

        foreach (var c in comments)
        {
            if (members.TryGetValue(c.AuthorId, out var author))
            {
                items.Add((c.IssueId, new FeedItem(
                    c.Id, FeedItemKind.Comment, author, c.Via, c.CreatedAt, [], c.Body, c.EditedAt, Meeting(c.ProposalId), c.SourceAuthor, SourceOf(c.IssueId, c.Via))));
            }
        }

        var byIssue = items.ToLookup(i => i.IssueId, i => i.Item);
        return issues.ToDictionary(i => i.Key, IReadOnlyList<FeedItem> (i) =>
            [.. byIssue[i.Id].OrderByDescending(f => f.At).ThenByDescending(f => f.Id).Take(limit).Reverse()]);
    }

    private static FeedItemKind? Kind(string kind) => kind switch
    {
        "created" => FeedItemKind.Created,
        "updated" => FeedItemKind.Updated,
        "relation_added" => FeedItemKind.RelationAdded,
        "relation_removed" => FeedItemKind.RelationRemoved,
        "sprint_closed" => FeedItemKind.SprintClosed,
        "split_from" => FeedItemKind.SplitFrom,
        "split_into" => FeedItemKind.SplitInto,
        "imported" => FeedItemKind.Imported,
        "reimported" => FeedItemKind.Reimported,
        "source_changed" => FeedItemKind.SourceChanged,
        _ => null,
    };

    private static string? Text(JsonElement data, string property) =>
        data.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;

    private static JsonElement? Parse(string? data) => data is null ? null : JsonSerializer.Deserialize<JsonElement>(data);

    /// <summary>GUIDs in a field's from/to values, for one batched name lookup per kind (no N+1).</summary>
    private static IEnumerable<Guid> Ids(JsonElement? data, string field)
    {
        if (data is not { ValueKind: JsonValueKind.Object } d || !d.TryGetProperty(field, out var change)
            || change.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var side in new[] { "from", "to" })
        {
            if (change.TryGetProperty(side, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var id))
            {
                yield return id;
            }
        }
    }

    private static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
