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
public sealed record FeedItem(
    Guid Id,
    FeedItemKind Kind,
    MemberRef Actor,
    Surface Via,
    DateTimeOffset At,
    IReadOnlyList<FeedChange> Changes,
    string? Body,
    DateTimeOffset? EditedAt,
    FeedMeeting? Meeting = null);

/// <summary>An issue's history and comments merged in time order (task 008).</summary>
public sealed class ActivityFeed(IDbContextFactory<KuproDbContext> dbFactory)
{
    /// <summary>Most recent entries kept per issue; older history is rarely read and keeps the response small (ADR-010).</summary>
    public const int Limit = 500;

    public async Task<IReadOnlyList<FeedItem>?> GetAsync(string key, CancellationToken ct) =>
        (await GetManyAsync([key], ct)).Values.SingleOrDefault();

    /// <summary>Feeds by issue key, for the issues that exist among <paramref name="keys"/>. The same queries however many keys.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<FeedItem>>> GetManyAsync(IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        var normalized = keys.Select(k => k.Trim().ToUpperInvariant()).Distinct().ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var issues = await db.Issues.Where(i => normalized.Contains(i.Key)).Select(i => new { i.Id, i.Key }).ToListAsync(ct);
        if (issues.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<FeedItem>>();
        }

        var issueIds = issues.Select(i => i.Id).ToList();
        var entries = await db.Issues.Where(i => issueIds.Contains(i.Id))
            .SelectMany(i => db.Activity.Where(a => a.IssueId == i.Id).OrderByDescending(a => a.CreatedAt).Take(Limit))
            .Select(a => new { a.Id, a.IssueId, a.Kind, a.ActorId, a.Via, a.Data, a.CreatedAt, a.ProposalId })
            .ToListAsync(ct);
        var comments = await db.Issues.Where(i => issueIds.Contains(i.Id))
            .SelectMany(i => db.Comments.Where(c => c.IssueId == i.Id).OrderByDescending(c => c.CreatedAt).Take(Limit))
            .Select(c => new { c.Id, c.IssueId, c.AuthorId, c.Via, c.Body, c.CreatedAt, c.EditedAt, c.ProposalId })
            .ToListAsync(ct);

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
                    : db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefault()!,
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
            if (kind is FeedItemKind.Updated && data is { ValueKind: JsonValueKind.Object } fields)
            {
                foreach (var f in fields.EnumerateObject())
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

            items.Add((e.IssueId, new FeedItem(e.Id, kind, actor, e.Via, e.CreatedAt, changes, null, null, Meeting(e.ProposalId))));
        }

        foreach (var c in comments)
        {
            if (members.TryGetValue(c.AuthorId, out var author))
            {
                items.Add((c.IssueId, new FeedItem(c.Id, FeedItemKind.Comment, author, c.Via, c.CreatedAt, [], c.Body, c.EditedAt, Meeting(c.ProposalId))));
            }
        }

        var byIssue = items.ToLookup(i => i.IssueId, i => i.Item);
        return issues.ToDictionary(i => i.Key, IReadOnlyList<FeedItem> (i) => byIssue[i.Id].OrderBy(f => f.At).ThenBy(f => f.Id).ToList());
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
        _ => null,
    };

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
