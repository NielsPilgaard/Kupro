using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace Kupro.Tests.Infrastructure;

/// <summary>
/// GitHub's REST API for connector tests (task 027), in the documented response shapes: repositories, org issue types,
/// assignees, milestones, issues (pull requests among them), sub-issues, comments and issue events, paged with
/// <c>Link</c> headers. Bearer token only.
/// </summary>
internal sealed class FakeGitHub(string api = "https://api.github.com/") : HttpMessageHandler
{
    public const string Token = "github_pat_test";

    public Uri Api { get; } = new(api);
    public List<JsonObject> Repos { get; } = [];
    public Dictionary<string, List<JsonObject>> IssueTypes { get; } = [];
    public Dictionary<string, List<JsonObject>> Assignees { get; } = [];
    public Dictionary<string, List<JsonObject>> Milestones { get; } = [];
    public Dictionary<string, List<JsonObject>> Issues { get; } = [];
    public Dictionary<string, List<int>> SubIssues { get; } = [];
    public Dictionary<string, List<JsonObject>> Comments { get; } = [];
    public Dictionary<string, List<JsonObject>> Events { get; } = [];
    public List<string> Requests { get; } = [];

    /// <summary>Items per page, so the connector has to follow <c>Link</c>.</summary>
    public int PageSize { get; set; } = 2;

    /// <summary>The next request is refused by the primary rate limit, once.</summary>
    public bool RateLimitOnce { get; set; }

    public static JsonObject User(long id, string login, string type = "User") => new() { ["id"] = id, ["login"] = login, ["type"] = type };

    public JsonObject AddRepo(long id, string owner, string name, string ownerType = "Organization", bool hasIssues = true)
    {
        var repo = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["full_name"] = $"{owner}/{name}",
            ["owner"] = User(id * 10, owner, ownerType),
            ["html_url"] = $"https://github.com/{owner}/{name}",
            ["has_issues"] = hasIssues,
        };
        Repos.Add(repo);
        return repo;
    }

    public JsonObject AddIssue(string repo, long id, int number, string title, DateTimeOffset created, DateTimeOffset? updated = null)
    {
        var issue = new JsonObject
        {
            ["id"] = id,
            ["number"] = number,
            ["title"] = title,
            ["state"] = "open",
            ["state_reason"] = null,
            ["user"] = User(1, "sofie"),
            ["assignees"] = new JsonArray(),
            ["labels"] = new JsonArray(),
            ["created_at"] = created,
            ["updated_at"] = updated ?? created,
            ["html_url"] = $"https://github.com/{repo}/issues/{number}",
        };
        List(Issues, repo).Add(issue);
        return issue;
    }

    public static JsonObject Label(string name) => new() { ["name"] = name };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        var path = url.AbsolutePath[Api.AbsolutePath.Length..];
        var query = HttpUtility.ParseQueryString(url.Query);
        lock (Requests)
        {
            Requests.Add($"{path}{Uri.UnescapeDataString(url.Query)}");
        }

        if (request.Headers.Authorization?.ToString() != $"Bearer {Token}")
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        if (request.Headers.UserAgent.Count == 0)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("User-Agent required") });
        }

        if (RateLimitOnce)
        {
            RateLimitOnce = false;
            var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
            limited.Headers.Add("x-ratelimit-remaining", "0");
            limited.Headers.Add("x-ratelimit-reset", "1893456000");
            return Task.FromResult(limited);
        }

        var parts = path.Split('/');
        return Task.FromResult(parts switch
        {
            ["user", "repos"] => Page(Repos, url, query),
            ["orgs", var org, "issue-types"] => IssueTypes.TryGetValue(org, out var types) ? Page(types, url, query) : NotFound(),
            ["repos", var o, var r, "assignees"] => Page(List(Assignees, $"{o}/{r}"), url, query),
            ["repos", var o, var r, "milestones"] => Page(List(Milestones, $"{o}/{r}"), url, query),
            ["repos", var o, var r, "issues"] => Page(Since(List(Issues, $"{o}/{r}"), query["since"], "updated_at"), url, query),
            ["repos", var o, var r, "issues", "comments"] => Page(Since(List(Comments, $"{o}/{r}"), query["since"], "updated_at"), url, query),
            ["repos", var o, var r, "issues", "events"] => Page(List(Events, $"{o}/{r}"), url, query),
            ["repos", var o, var r, "issues", var n, "sub_issues"] => Page(
                [.. List(SubIssues, $"{o}/{r}#{n}").Select(child => List(Issues, $"{o}/{r}").Single(i => (int)i["number"]! == child))], url, query),
            _ => NotFound(),
        });
    }

    private static List<T> List<T>(Dictionary<string, List<T>> lists, string key) => lists.TryGetValue(key, out var list) ? list : lists[key] = [];

    private static List<JsonObject> Since(List<JsonObject> items, string? since, string field) =>
        since is null ? items : [.. items.Where(i => i[field]!.GetValue<DateTimeOffset>() >= DateTimeOffset.Parse(since, CultureInfo.InvariantCulture))];

    private HttpResponseMessage Page(List<JsonObject> items, Uri url, System.Collections.Specialized.NameValueCollection query)
    {
        var page = int.TryParse(query["page"], CultureInfo.InvariantCulture, out var p) ? p : 1;
        var slice = items.Skip((page - 1) * PageSize).Take(PageSize).Select(i => (JsonNode)i.DeepClone()).ToArray();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonArray(slice).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (page * PageSize < items.Count)
        {
            query["page"] = (page + 1).ToString(CultureInfo.InvariantCulture);
            var next = new UriBuilder(url) { Query = query.ToString() }.Uri;
            response.Headers.Add("Link", $"<{next.AbsoluteUri}>; rel=\"next\", <{url.AbsoluteUri}>; rel=\"first\"");
        }

        return response;
    }

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"Not Found"}""") };
}
