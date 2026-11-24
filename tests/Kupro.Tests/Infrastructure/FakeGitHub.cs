using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace Kupro.Tests.Infrastructure;

/// <summary>
/// GitHub's REST API for connector tests (task 027), in the documented response shapes: repositories, org issue types,
/// assignees, milestones, issues (pull requests among them), sub-issues, comments and issue events, paged with
/// <c>Link</c> headers; and GraphQL for Projects (v2) boards, their fields and items. Bearer token only.
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
    public Dictionary<string, List<int>> BlockedBy { get; } = [];
    public Dictionary<string, List<JsonObject>> Comments { get; } = [];
    public Dictionary<string, List<JsonObject>> Events { get; } = [];
    public List<string> Requests { get; } = [];

    /// <summary>Projects (v2) boards by owner login: <c>id</c>, <c>title</c>, <c>fields</c> and <c>items</c> (see <see cref="AddBoard"/>).</summary>
    public Dictionary<string, List<JsonObject>> Boards { get; } = [];

    /// <summary>Errors every board items page answers with alongside its data, as GitHub does for parts it couldn't read.</summary>
    public List<JsonObject> ItemErrors { get; } = [];

    /// <summary>The token's user, as <c>GET /user</c> answers; null answers 404.</summary>
    public JsonObject? Viewer { get; set; }

    /// <summary>Owners whose boards the token may not read: GraphQL answers with a FORBIDDEN error for them.</summary>
    public HashSet<string> BoardsForbidden { get; } = [];

    /// <summary>The next GraphQL query is refused by the primary rate limit (200 with a RATE_LIMITED error), once.</summary>
    public bool GraphQLRateLimitOnce { get; set; }

    /// <summary>Every GraphQL query fails whole, as for a classic token without read:project (no data, an error).</summary>
    public bool GraphQLScopeMissing { get; set; }

    /// <summary>Items per page, so the connector has to follow <c>Link</c>.</summary>
    public int PageSize { get; set; } = 2;

    /// <summary>The next request is refused by the primary rate limit, once.</summary>
    public bool RateLimitOnce { get; set; }

    /// <summary>The next request is refused by a secondary rate limit that names itself only in the body, once.</summary>
    public bool SecondaryLimitOnce { get; set; }

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

    /// <summary>A board with a Status field, a two-week Iteration field, an Estimate and a Budget number field.</summary>
    public JsonObject AddBoard(string owner, string id, string title, params string[] statuses)
    {
        var board = new JsonObject
        {
            ["id"] = id,
            ["title"] = title,
            ["url"] = $"https://github.com/orgs/{owner}/projects/1",
            ["fields"] = new JsonArray(
                new JsonObject { ["id"] = $"{id}-title", ["name"] = "Title", ["dataType"] = "TITLE" },
                new JsonObject
                {
                    ["id"] = $"{id}-status",
                    ["name"] = "Status",
                    ["dataType"] = "SINGLE_SELECT",
                    ["options"] = new JsonArray([.. statuses.Select(s => (JsonNode)new JsonObject { ["id"] = OptionId(s), ["name"] = s })]),
                },
                new JsonObject
                {
                    ["id"] = $"{id}-sprint",
                    ["name"] = "Sprint",
                    ["dataType"] = "ITERATION",
                    ["configuration"] = new JsonObject
                    {
                        ["iterations"] = new JsonArray(new JsonObject { ["id"] = "it2", ["title"] = "Sprint 2", ["startDate"] = "2025-03-15", ["duration"] = 14 }),
                        ["completedIterations"] = new JsonArray(new JsonObject { ["id"] = "it1", ["title"] = "Sprint 1", ["startDate"] = "2025-03-01", ["duration"] = 14 }),
                    },
                },
                new JsonObject { ["id"] = $"{id}-estimate", ["name"] = "Estimate", ["dataType"] = "NUMBER" },
                new JsonObject { ["id"] = $"{id}-other", ["name"] = "Budget", ["dataType"] = "NUMBER" }),
            ["items"] = new JsonArray(),
        };
        List(Boards, owner).Add(board);
        return board;
    }

    /// <summary>A status option's ID in <see cref="AddBoard"/>: its name, lowercased and dashed.</summary>
    public static string OptionId(string status) => status.ToLowerInvariant().Replace(' ', '-');

    /// <summary>Puts an issue (<c>owner/repo#n</c>) on a board with a status, iteration and estimate; null leaves one out.</summary>
    public static void AddBoardItem(JsonObject board, string issue, string? status, string? iteration, decimal? estimate, DateTimeOffset updated, bool archived = false)
    {
        var id = (string)board["id"]!;
        JsonObject Value(string field, string key, JsonNode value) => new() { [key] = value, ["field"] = new JsonObject { ["id"] = $"{id}-{field}" } };
        // A value type the query has no fragment for comes back empty.
        var values = new JsonArray(new JsonObject());
        if (status is not null)
        {
            values.Add(Value("status", "optionId", OptionId(status)));
        }

        if (iteration is not null)
        {
            values.Add(Value("sprint", "iterationId", iteration));
        }

        if (estimate is not null)
        {
            values.Add(Value("estimate", "number", estimate.Value));
            values.Add(Value("other", "number", 999));
        }

        var parts = issue.Split('#');
        board["items"]!.AsArray().Add(new JsonObject
        {
            ["isArchived"] = archived,
            ["updatedAt"] = updated,
            ["content"] = new JsonObject { ["number"] = int.Parse(parts[1], CultureInfo.InvariantCulture), ["repository"] = new JsonObject { ["nameWithOwner"] = parts[0] } },
            ["fieldValues"] = new JsonObject { ["nodes"] = values },
        });
    }

    public static JsonObject Label(string name) => new() { ["name"] = name };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        // GraphQL is logged by its full path: /graphql, or /api/graphql on a server.
        var path = request.Method == HttpMethod.Post && url.AbsolutePath.EndsWith("/graphql", StringComparison.Ordinal) ? url.AbsolutePath : url.AbsolutePath[Api.AbsolutePath.Length..];
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

        if (SecondaryLimitOnce)
        {
            SecondaryLimitOnce = false;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again."}"""),
            });
        }

        if (path.EndsWith("/graphql", StringComparison.Ordinal))
        {
            return GraphQLAsync(request, cancellationToken);
        }

        var parts = path.Split('/');
        return Task.FromResult(parts switch
        {
            ["user"] => Viewer is { } viewer ? Json(viewer) : NotFound(),
            ["user", "repos"] => Page(Repos, url, query),
            ["orgs", var org, "issue-types"] => IssueTypes.TryGetValue(org, out var types) ? Page(types, url, query) : NotFound(),
            ["repos", var o, var r, "assignees"] => Page(List(Assignees, $"{o}/{r}"), url, query),
            ["repos", var o, var r, "milestones"] => Page(List(Milestones, $"{o}/{r}"), url, query),
            ["repos", var o, var r, "issues"] => Page(Since(List(Issues, $"{o}/{r}"), query["since"], "updated_at"), url, query),
            ["repos", var o, var r, "issues", "comments"] => Page(Since(List(Comments, $"{o}/{r}"), query["since"], "updated_at"), url, query),
            ["repos", var o, var r, "issues", "events"] => Page(List(Events, $"{o}/{r}"), url, query),
            ["repos", var o, var r, "issues", var n] when int.TryParse(n, CultureInfo.InvariantCulture, out var number) =>
                List(Issues, $"{o}/{r}").FirstOrDefault(i => (int)i["number"]! == number) is { } issue ? Json(issue) : NotFound(),
            ["repos", var o, var r, "issues", var n, "dependencies", "blocked_by"] => Page(
                [.. List(BlockedBy, $"{o}/{r}#{n}").Select(blocker => List(Issues, $"{o}/{r}").Single(i => (int)i["number"]! == blocker))], url, query),
            ["repos", var o, var r, "issues", var n, "parent"] => SubIssues.FirstOrDefault(s => s.Key.StartsWith($"{o}/{r}#", StringComparison.Ordinal)
                && s.Value.Contains(int.Parse(n, CultureInfo.InvariantCulture))) is { Key: { } parent }
                ? Json(List(Issues, $"{o}/{r}").Single(i => (int)i["number"]! == int.Parse(parent.Split('#')[1], CultureInfo.InvariantCulture)))
                : NotFound(),
            ["repos", var o, var r, "issues", var n, "sub_issues"] => Page(
                [.. List(SubIssues, $"{o}/{r}#{n}").Select(child => List(Issues, $"{o}/{r}").Single(i => (int)i["number"]! == child))], url, query),
            _ => NotFound(),
        });
    }

    /// <summary>Answers the connector's three queries by what they ask for, paging with the index as the cursor.</summary>
    private async Task<HttpResponseMessage> GraphQLAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
        var query = (string)body["query"]!;
        var variables = body["variables"]!.AsObject();
        var after = (string?)variables["after"] is { } a ? int.Parse(a, CultureInfo.InvariantCulture) : 0;
        JsonObject PageOf(IEnumerable<JsonObject> all, Func<JsonObject, JsonNode?> shape)
        {
            var list = all.ToList();
            return new JsonObject
            {
                ["pageInfo"] = new JsonObject { ["hasNextPage"] = after + PageSize < list.Count, ["endCursor"] = (after + PageSize).ToString(CultureInfo.InvariantCulture) },
                ["nodes"] = new JsonArray([.. list.Skip(after).Take(PageSize).Select(shape)]),
            };
        }

        if (GraphQLRateLimitOnce || GraphQLScopeMissing)
        {
            var limited = GraphQLRateLimitOnce;
            GraphQLRateLimitOnce = false;
            var refused = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(limited
                    ? """{"errors":[{"type":"RATE_LIMITED","message":"API rate limit exceeded"}]}"""
                    : """{"errors":[{"type":"INSUFFICIENT_SCOPES","message":"Your token has not been granted the required scopes to execute this query."}]}""", Encoding.UTF8, "application/json"),
            };
            if (limited)
            {
                refused.Headers.Add("x-ratelimit-remaining", "0");
                refused.Headers.Add("x-ratelimit-reset", "1893456000");
            }

            return refused;
        }

        var board = Boards.Values.SelectMany(b => b).FirstOrDefault(b => (string)b["id"]! == (string?)variables["board"]);
        JsonObject data;
        var errors = new JsonArray();
        if (query.Contains("repositoryOwner", StringComparison.Ordinal))
        {
            var login = (string)variables["login"]!;
            JsonObject? projects = null;
            if (BoardsForbidden.Contains(login))
            {
                errors.Add(new JsonObject { ["type"] = "FORBIDDEN", ["message"] = "Resource not accessible by personal access token" });
            }
            else
            {
                projects = PageOf(List(Boards, login), b => new JsonObject
                {
                    ["id"] = b["id"]!.DeepClone(),
                    ["title"] = b["title"]!.DeepClone(),
                    ["url"] = b["url"]!.DeepClone(),
                    ["field"] = b["fields"]!.AsArray().OfType<JsonObject>().FirstOrDefault(f => (string?)f["name"] == "Status")?.DeepClone(),
                });
            }

            // projectsV2 is non-null in the schema, so a refusal nulls the owner.
            data = new JsonObject { ["repositoryOwner"] = projects is null ? null : new JsonObject { ["projectsV2"] = projects } };
        }
        else
        {
            if (board is null)
            {
                errors.Add(new JsonObject { ["type"] = "NOT_FOUND", ["message"] = $"Could not resolve to a node with the global id of '{variables["board"]}'" });
            }

            if (board is not null && query.Contains("items(", StringComparison.Ordinal))
            {
                foreach (var error in ItemErrors)
                {
                    errors.Add(error.DeepClone());
                }
            }

            data = new JsonObject
            {
                ["node"] = board is null ? null
                    : query.Contains("items(", StringComparison.Ordinal) ? new JsonObject { ["items"] = PageOf(board["items"]!.AsArray().OfType<JsonObject>(), i => i.DeepClone()) }
                    : new JsonObject { ["fields"] = new JsonObject { ["nodes"] = board["fields"]!.DeepClone() } },
            };
        }

        var answer = new JsonObject { ["data"] = data };
        if (errors.Count > 0)
        {
            answer["errors"] = errors;
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json") };
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

    private static HttpResponseMessage Json(JsonObject item) =>
        new(HttpStatusCode.OK) { Content = new StringContent(item.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"Not Found"}""") };
}
