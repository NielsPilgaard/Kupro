using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kupro.Tests.Infrastructure;

/// <summary>
/// A Jira site for connector tests (task 024): Cloud (platform API v3, token-paged <c>search/jql</c>, Basic auth with
/// email and API token) or Data Center (v2, <c>startAt</c> paging, a PAT as Bearer), in the documented response shapes.
/// Like the real thing, a search embeds only the first page of a long changelog or comment list, and the relative
/// <c>updated &gt;= "-Nm"</c> JQL is read against <see cref="Now"/>. Every request is logged for assertions.
/// </summary>
internal sealed partial class FakeJira : HttpMessageHandler
{
    public const string CloudToken = "sofie@acme.example:api-token";
    public const string DataCenterToken = "dc-personal-access-token";

    public FakeJira(bool cloud)
    {
        Cloud = cloud;
        Site = new Uri(cloud ? "https://acme.atlassian.net" : "https://jira.acme.example/jira");
    }

    public bool Cloud { get; }
    public Uri Site { get; }
    public string Api => Cloud ? "3" : "2";
    public string SiteUrl => Site.AbsoluteUri.TrimEnd('/');

    public DateTimeOffset Now { get; set; } = new(2025, 3, 20, 12, 0, 0, TimeSpan.Zero);

    public List<JsonObject> Projects { get; } = [];
    public List<JsonObject> IssueTypes { get; } = [];
    public Dictionary<string, JsonArray> Workflows { get; } = [];
    public List<JsonObject> Fields { get; } = [];
    public Dictionary<string, JsonArray> Versions { get; } = [];
    public List<FakeJiraIssue> Issues { get; } = [];
    public Dictionary<long, JsonObject> Sprints { get; } = [];
    public Dictionary<string, byte[]> Files { get; } = [];

    /// <summary>How many changelog entries and comments a search embeds before the client must page.</summary>
    public int Embedded { get; set; } = 2;

    /// <summary>The next N requests are answered 429 with <see cref="RetryAfter"/>.</summary>
    public int Throttle { get; set; }

    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(7);

    public List<string> Requests { get; } = [];
    public List<string> ForeignHosts { get; } = [];

    public HttpClient Client() => new(this);

    public static JsonObject User(string id, string name, string? email = null, bool cloud = true, string type = "atlassian") => cloud
        ? new JsonObject { ["accountId"] = id, ["displayName"] = name, ["emailAddress"] = email, ["accountType"] = type }
        : new JsonObject { ["key"] = id, ["name"] = name.Split(' ')[0].ToLowerInvariant(), ["displayName"] = name, ["emailAddress"] = email };

    public static string Time(DateTimeOffset time) => time.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture) + "+0000";

    public JsonObject AddProject(string id, string key, string name, string type = "software")
    {
        var project = new JsonObject { ["id"] = id, ["key"] = key, ["name"] = name, ["projectTypeKey"] = type };
        Projects.Add(project);
        Workflows[id] = [];
        Versions[id] = [];
        return project;
    }

    public FakeJiraIssue AddIssue(string projectId, string id, string key, string type, string status, string summary, DateTimeOffset updated)
    {
        var issue = new FakeJiraIssue(this, id, key, projectId, updated);
        issue.Fields["summary"] = summary;
        issue.Fields["issuetype"] = new JsonObject { ["id"] = type };
        issue.Fields["status"] = new JsonObject { ["id"] = status };
        issue.Fields["created"] = Time(updated.AddDays(-1));
        Issues.Add(issue);
        return issue;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        if (!url.Authority.Equals(Site.Authority, StringComparison.OrdinalIgnoreCase))
        {
            lock (ForeignHosts)
            {
                ForeignHosts.Add(url.Authority);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var expected = Cloud
            ? "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(CloudToken))
            : "Bearer " + DataCenterToken;
        var path = url.AbsolutePath[Site.AbsolutePath.TrimEnd('/').Length..].TrimStart('/');
        if (path != "rest/api/2/serverInfo" && request.Headers.Authorization?.ToString() != expected)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        lock (Requests)
        {
            Requests.Add($"{request.Method} {path}{Uri.UnescapeDataString(url.Query)}");
            if (Throttle > 0)
            {
                Throttle--;
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(RetryAfter);
                return Task.FromResult(throttled);
            }
        }

        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        return Task.FromResult(Route(path, query));
    }

    private HttpResponseMessage Route(string path, System.Collections.Specialized.NameValueCollection query)
    {
        var api = $"rest/api/{Api}/";
        if (path == "rest/api/2/serverInfo")
        {
            return Json(new JsonObject { ["baseUrl"] = SiteUrl, ["version"] = Cloud ? "1001.0.0" : "9.12.4", ["deploymentType"] = Cloud ? "Cloud" : "Server" });
        }

        if (Cloud && path == "rest/api/3/project/search")
        {
            var start = Int(query["startAt"]);
            var size = Int(query["maxResults"], 50);
            var page = Projects.Skip(start).Take(size).ToList();
            return Json(new JsonObject { ["values"] = Array(page), ["isLast"] = start + size >= Projects.Count });
        }

        if (!Cloud && path == "rest/api/2/project")
        {
            return Json(Array(Projects));
        }

        if (path == api + "issuetype")
        {
            return Json(Array(IssueTypes));
        }

        if (path == api + "field")
        {
            return Json(Array(Fields));
        }

        if (ProjectPath().Match(path) is { Success: true } project && project.Groups["api"].Value == Api)
        {
            var id = project.Groups["id"].Value;
            return project.Groups["what"].Value == "statuses" ? Json(Workflows[id].DeepClone()) : Json(Versions[id].DeepClone());
        }

        if (Cloud && path == "rest/api/3/search/jql")
        {
            var found = Search(query["jql"]!);
            var start = int.TryParse(query["nextPageToken"], CultureInfo.InvariantCulture, out var token) ? token : 0;
            var size = Int(query["maxResults"], 50);
            var page = found.Skip(start).Take(size).ToList();
            var body = new JsonObject { ["issues"] = Array(page.Select(i => i.Json(Embedded, query["expand"] == "changelog"))), ["isLast"] = start + size >= found.Count };
            if (start + size < found.Count)
            {
                body["nextPageToken"] = (start + size).ToString(CultureInfo.InvariantCulture);
            }

            return Json(body);
        }

        if (!Cloud && path == "rest/api/2/search")
        {
            var found = Search(query["jql"]!);
            var start = Int(query["startAt"]);
            var size = Int(query["maxResults"], 50);
            var page = found.Skip(start).Take(size).ToList();
            return Json(new JsonObject
            {
                ["startAt"] = start,
                ["maxResults"] = size,
                ["total"] = found.Count,
                ["issues"] = Array(page.Select(i => i.Json(Embedded, query["expand"] == "changelog"))),
            });
        }

        if (IssuePath().Match(path) is { Success: true } issuePath && issuePath.Groups["api"].Value == Api)
        {
            var idOrKey = issuePath.Groups["id"].Value;
            var issue = Issues.FirstOrDefault(i => i.Id == idOrKey || i.Key == idOrKey);
            if (issue is null)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var start = Int(query["startAt"]);
            var size = Int(query["maxResults"], 50);
            switch (issuePath.Groups["what"].Value)
            {
                case "":
                    return Json(issue.Json(int.MaxValue, query["expand"] == "changelog"));
                case "/changelog" when Cloud:
                    var histories = issue.Histories.Skip(start).Take(size).ToList();
                    return Json(new JsonObject
                    {
                        ["startAt"] = start,
                        ["maxResults"] = size,
                        ["total"] = issue.Histories.Count,
                        ["isLast"] = start + size >= issue.Histories.Count,
                        ["values"] = Array(histories),
                    });
                case "/comment":
                    return Json(new JsonObject
                    {
                        ["startAt"] = start,
                        ["maxResults"] = size,
                        ["total"] = issue.Comments.Count,
                        ["comments"] = Array(issue.Comments.Skip(start).Take(size)),
                    });
            }
        }

        if (SprintPath().Match(path) is { Success: true } sprint)
        {
            return Sprints.TryGetValue(long.Parse(sprint.Groups[1].Value, CultureInfo.InvariantCulture), out var found)
                ? Json(found.DeepClone())
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (FilePath().Match(path) is { Success: true } file && Files.TryGetValue(file.Groups["id"].Value, out var bytes))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    /// <summary>The JQL the connector sends: <c>project = ID [AND updated &gt;= "-Nm"] ORDER BY key ASC</c>.</summary>
    private List<FakeJiraIssue> Search(string jql)
    {
        var match = Jql().Match(jql);
        if (!match.Success)
        {
            throw new InvalidOperationException($"The fake doesn't understand: {jql}");
        }

        var since = match.Groups["minutes"].Success ? Now.AddMinutes(-int.Parse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture)) : (DateTimeOffset?)null;
        return [.. Issues
            .Where(i => i.ProjectId == match.Groups["project"].Value && (since is null || i.Updated >= since))
            .OrderBy(i => i.Key.Split('-')[0], StringComparer.Ordinal)
            .ThenBy(i => int.Parse(i.Key.Split('-')[1], CultureInfo.InvariantCulture))];
    }

    public string ContentUrl(string id, string name) =>
        Cloud ? $"{SiteUrl}/rest/api/3/attachment/content/{id}" : $"{SiteUrl}/secure/attachment/{id}/{Uri.EscapeDataString(name)}";

    private static int Int(string? text, int fallback = 0) => int.TryParse(text, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static JsonArray Array(IEnumerable<JsonNode> nodes) => [.. nodes.Select(n => n.DeepClone())];

    private static HttpResponseMessage Json(JsonNode body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    [GeneratedRegex(@"^project = (?<project>\d+)(?: AND updated >= ""-(?<minutes>\d+)m"")? ORDER BY key ASC$")]
    private static partial Regex Jql();

    [GeneratedRegex(@"^rest/api/(?<api>\d)/project/(?<id>\d+)/(?<what>statuses|versions)$")]
    private static partial Regex ProjectPath();

    [GeneratedRegex(@"^rest/api/(?<api>\d)/issue/(?<id>[^/]+)(?<what>/changelog|/comment|)$")]
    private static partial Regex IssuePath();

    [GeneratedRegex(@"^rest/agile/1\.0/sprint/(\d+)$")]
    private static partial Regex SprintPath();

    [GeneratedRegex(@"^(?:rest/api/3/attachment/content/(?<id>\d+)|secure/attachment/(?<id>\d+)/.+)$")]
    private static partial Regex FilePath();
}

internal sealed class FakeJiraIssue(FakeJira jira, string id, string key, string projectId, DateTimeOffset updated)
{
    public string Id { get; } = id;
    public string Key { get; } = key;
    public string ProjectId { get; } = projectId;
    public DateTimeOffset Updated { get; private set; } = updated;
    public JsonObject Fields { get; } = [];
    public List<JsonObject> Histories { get; } = [];
    public List<JsonObject> Comments { get; } = [];

    public FakeJiraIssue Set(string field, JsonNode? value)
    {
        Fields[field] = value;
        return this;
    }

    /// <summary>An edit in Jira: moves <c>updated</c> on.</summary>
    public FakeJiraIssue Touch(DateTimeOffset at)
    {
        Updated = at;
        return this;
    }

    public FakeJiraIssue Link(string linkId, string type, string outward, string inward, string? outwardIssue = null, string? inwardIssue = null)
    {
        var links = Fields["issuelinks"] as JsonArray ?? [];
        Fields["issuelinks"] = links;
        var link = new JsonObject { ["id"] = linkId, ["type"] = new JsonObject { ["name"] = type, ["outward"] = outward, ["inward"] = inward } };
        if (outwardIssue is not null)
        {
            link["outwardIssue"] = new JsonObject { ["id"] = outwardIssue };
        }

        if (inwardIssue is not null)
        {
            link["inwardIssue"] = new JsonObject { ["id"] = inwardIssue };
        }

        links.Add(link);
        return this;
    }

    public FakeJiraIssue Attach(string fileId, string name, string mime, byte[] bytes, JsonObject author, DateTimeOffset created)
    {
        var files = Fields["attachment"] as JsonArray ?? [];
        Fields["attachment"] = files;
        files.Add(new JsonObject
        {
            ["id"] = fileId,
            ["filename"] = name,
            ["mimeType"] = mime,
            ["size"] = bytes.Length,
            ["author"] = author,
            ["created"] = FakeJira.Time(created),
            ["content"] = jira.ContentUrl(fileId, name),
        });
        jira.Files[fileId] = bytes;
        return this;
    }

    public FakeJiraIssue History(string historyId, JsonObject author, DateTimeOffset at, params (string Field, string? FieldId, string? From, string? FromText, string? To, string? ToText)[] items)
    {
        Histories.Add(new JsonObject
        {
            ["id"] = historyId,
            ["author"] = author,
            ["created"] = FakeJira.Time(at),
            ["items"] = new JsonArray([.. items.Select(i => (JsonNode)new JsonObject
            {
                ["field"] = i.Field,
                ["fieldtype"] = i.FieldId?.StartsWith("customfield_", StringComparison.Ordinal) == true ? "custom" : "jira",
                ["fieldId"] = i.FieldId,
                ["from"] = i.From,
                ["fromString"] = i.FromText,
                ["to"] = i.To,
                ["toString"] = i.ToText,
            })]),
        });
        return this;
    }

    public FakeJiraIssue Comment(string commentId, JsonNode body, JsonObject author, DateTimeOffset at)
    {
        Comments.Add(new JsonObject { ["id"] = commentId, ["author"] = author, ["body"] = body, ["created"] = FakeJira.Time(at), ["updated"] = FakeJira.Time(at) });
        return this;
    }

    /// <summary>The issue as a search or issue GET returns it; the changelog and comments cut at <paramref name="embedded"/>.</summary>
    public JsonObject Json(int embedded, bool changelog)
    {
        var fields = (JsonObject)Fields.DeepClone();
        fields["updated"] = FakeJira.Time(Updated);
        fields["comment"] = new JsonObject
        {
            ["startAt"] = 0,
            ["maxResults"] = embedded,
            ["total"] = Comments.Count,
            ["comments"] = new JsonArray([.. Comments.Take(embedded).Select(c => c.DeepClone())]),
        };
        var issue = new JsonObject { ["id"] = Id, ["key"] = Key, ["fields"] = fields };
        if (changelog)
        {
            issue["changelog"] = new JsonObject
            {
                ["startAt"] = 0,
                ["maxResults"] = embedded,
                ["total"] = Histories.Count,
                ["histories"] = new JsonArray([.. Histories.Take(embedded).Select(h => h.DeepClone())]),
            };
        }

        return issue;
    }
}
