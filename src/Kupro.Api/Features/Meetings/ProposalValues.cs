using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kupro.Api.Data;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Sprints;
using Kupro.Api.Features.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Api.Features.Meetings;

/// <summary>
/// What the meeting agent and voice control (task 036) share about a heard action: issue references matched by our own
/// search (never by the model), values made canonical, and a field's current value for diffs and undo.
/// </summary>
internal static partial class ProposalValues
{
    public const int MaxCandidates = 3;

    /// <summary>An exact key wins; otherwise search the project (or every project). Several matches become candidates, never a guess.</summary>
    public static async Task<(Guid? Id, List<string> Candidates)> ResolveIssueAsync(
        KuproDbContext db, IssueService issues, string? projectKey, string reference, CancellationToken ct)
    {
        var text = reference.Trim();
        if (IssueKey().IsMatch(text.ToUpperInvariant()))
        {
            var key = text.ToUpperInvariant();
            if (await db.Issues.Where(i => i.Key == key).Select(i => (Guid?)i.Id).SingleOrDefaultAsync(ct) is { } exact)
            {
                return (exact, []);
            }
        }

        var words = Article().Replace(text, "").Trim();
        if (words.Length < 2)
        {
            return (null, []);
        }

        var found = await issues.ListAsync(new IssueQuery
        {
            Q = words,
            Project = projectKey is null ? null : [projectKey],
            State = [IssueState.Backlog, IssueState.Todo, IssueState.InProgress, IssueState.InReview],
            Limit = MaxCandidates,
        }, ct);
        var items = found.Value?.Items ?? [];
        return items.Count == 1 ? (items[0].Id, []) : (null, items.Select(i => i.Key).ToList());
    }

    /// <summary>
    /// Canonical value for an operation, or null if it can't be resolved. Sprints are looked up in <paramref name="projectId"/>;
    /// without one only <c>none</c> resolves. "Me" is <paramref name="memberId"/>.
    /// </summary>
    public static async Task<string?> NormalizeAsync(KuproDbContext db, Guid? projectId, Guid memberId, ProposalOperation operation,
        string? raw, CancellationToken ct)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        switch (operation)
        {
            case ProposalOperation.State:
                return Enum.TryParse<IssueState>(value.Replace(" ", ""), ignoreCase: true, out var state) && Enum.IsDefined(state)
                    ? Camel(state)
                    : null;
            case ProposalOperation.Estimate:
                return value.Equals("none", StringComparison.OrdinalIgnoreCase) ? "none"
                    : decimal.TryParse(value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var hours) && hours is >= 0 and <= 100_000
                        ? hours.ToString(CultureInfo.InvariantCulture)
                        : null;
            case ProposalOperation.Comment:
                return Truncate(value, 4000);
            case ProposalOperation.CreateIssue:
                return Truncate(value, 500);
            case ProposalOperation.Sprint:
                if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    return "none";
                }

                if (projectId is null)
                {
                    return null;
                }

                var sprints = db.Sprints.Where(s => s.ProjectId == projectId && s.State != SprintState.Closed);
                var sprintId = value.ToLowerInvariant() switch
                {
                    "next" => await sprints.Where(s => s.State == SprintState.Planned).OrderBy(s => s.StartDate).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct),
                    "current" or "active" => await sprints.Where(s => s.State == SprintState.Active).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct),
                    _ when Guid.TryParse(value, out var id) => await sprints.Where(s => s.Id == id).Select(s => (Guid?)s.Id).SingleOrDefaultAsync(ct),
                    _ => await sprints.Where(s => EF.Functions.ILike(s.Name, value)).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct),
                };
                return sprintId?.ToString();
            case ProposalOperation.Assignee:
                if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    return "none";
                }

                if (value.Equals("me", StringComparison.OrdinalIgnoreCase) || value.Equals("mig", StringComparison.OrdinalIgnoreCase))
                {
                    return memberId.ToString();
                }

                return MatchPerson(await PeopleAsync(db, ct), value)?.Id.ToString();
            default:
                return null;
        }
    }

    /// <summary>A member by id, full name, or a unique first name ("give it to Anna").</summary>
    public static MemberRef? MatchPerson(IReadOnlyList<MemberRef> people, string value)
    {
        if (Guid.TryParse(value, out var memberId))
        {
            return people.FirstOrDefault(p => p.Id == memberId);
        }

        var exact = people.Where(p => p.Name.Equals(value, StringComparison.OrdinalIgnoreCase)).ToList();
        var first = people.Where(p => p.Name.Split(' ')[0].Equals(value.Split(' ')[0], StringComparison.OrdinalIgnoreCase)).ToList();
        return (exact.Count == 1 ? exact : first.Count == 1 ? first : []).SingleOrDefault();
    }

    public static Task<List<MemberRef>> PeopleAsync(KuproDbContext db, CancellationToken ct) =>
        db.Members.AsNoTracking().Where(m => m.Status == MemberStatus.Active).Select(MemberRefs.Projection(db)).ToListAsync(ct);

    /// <summary>The field's current canonical value, for diffs and undo.</summary>
    public static async Task<string?> CurrentAsync(KuproDbContext db, Guid issueId, ProposalOperation operation, CancellationToken ct)
    {
        var issue = await db.Issues.AsNoTracking().Where(i => i.Id == issueId)
            .Select(i => new { i.State, i.SprintId, i.AssigneeId, i.Effort }).SingleOrDefaultAsync(ct);
        return issue is null ? null : operation switch
        {
            ProposalOperation.State => Camel(issue.State),
            ProposalOperation.Sprint => issue.SprintId?.ToString() ?? "none",
            ProposalOperation.Assignee => issue.AssigneeId?.ToString() ?? "none",
            ProposalOperation.Estimate => issue.Effort?.ToString(CultureInfo.InvariantCulture) ?? "none",
            _ => null,
        };
    }

    /// <summary>The issue change a field operation makes, as <c>PATCH /issues/{key}</c> takes it.</summary>
    public static UpdateIssueRequest Change(ProposalOperation operation, string value) => operation switch
    {
        ProposalOperation.State => new UpdateIssueRequest(State: Enum.Parse<IssueState>(value, ignoreCase: true)),
        ProposalOperation.Sprint when value == "none" => new UpdateIssueRequest(Clear: [IssueField.Sprint]),
        ProposalOperation.Sprint => new UpdateIssueRequest(Sprint: value),
        ProposalOperation.Assignee when value == "none" => new UpdateIssueRequest(Clear: [IssueField.Assignee]),
        ProposalOperation.Assignee => new UpdateIssueRequest(Assignee: value),
        ProposalOperation.Estimate when value == "none" => new UpdateIssueRequest(Clear: [IssueField.Effort]),
        ProposalOperation.Estimate => new UpdateIssueRequest(Effort: decimal.Parse(value, CultureInfo.InvariantCulture)),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Not a field change."),
    };

    /// <summary>"kup 12" and "kup-12" in speech become KUP-12.</summary>
    public static string NormalizeKeys(string text, string projectKey) =>
        Regex.Replace(text, $@"\b{Regex.Escape(projectKey)}[\s-]?(\d+)\b", m => $"{projectKey}-{m.Groups[1].Value}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool IsIssueKey(string text) => IssueKey().IsMatch(text);

    public static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    public static string Camel<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    [GeneratedRegex(@"^[A-Z][A-Z0-9]{1,9}-\d+$")]
    private static partial Regex IssueKey();

    [GeneratedRegex(@"^(?:the|den|det)\s+|\s+(?:issue|ticket|task|opgave|sag)$", RegexOptions.IgnoreCase)]
    private static partial Regex Article();
}
