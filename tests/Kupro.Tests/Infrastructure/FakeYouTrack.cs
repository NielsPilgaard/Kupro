using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace Kupro.Tests.Infrastructure;

/// <summary>
/// A YouTrack REST API for connector tests (task 027), in the documented response shapes: projects with their custom
/// fields, users, agile boards with sprints, issues found by query, paged activities and signed file URLs. Bearer only.
/// </summary>
internal sealed class FakeYouTrack(string site = "https://acme.youtrack.cloud") : HttpMessageHandler
{
    public const string Token = "perm:test";

    public string Site { get; } = site;
    public List<JsonObject> Projects { get; } = [];
    public Dictionary<string, List<JsonObject>> CustomFields { get; } = [];
    public List<JsonObject> Users { get; } = [];
    public List<JsonObject> Agiles { get; } = [];
    public Dictionary<string, JsonObject> Sprints { get; } = [];

    /// <summary>Issues with the project's short name, for the query filter.</summary>
    public List<(string Project, JsonObject Issue)> Issues { get; } = [];

    public Dictionary<string, List<JsonObject>> Activities { get; } = [];
    public Dictionary<string, byte[]> Files { get; } = [];
    public List<string> Requests { get; } = [];

    /// <summary>Activities per page, so the connector has to follow <c>afterCursor</c>.</summary>
    public int ActivityPage { get; set; } = 2;

    public static long Ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    public static JsonObject User(string id, string login, string fullName, string? email = null) =>
        new() { ["id"] = id, ["login"] = login, ["fullName"] = fullName, ["email"] = email };

    public static JsonObject Field(string name, JsonNode? value) => new() { ["name"] = name, ["value"] = value };

    public static JsonObject Named(string name) => new() { ["name"] = name };

    public static JsonObject ProjectField(string name, string type, bool multi = false, params (string Name, bool? Resolved)[] values) => new()
    {
        ["field"] = new JsonObject { ["name"] = name, ["fieldType"] = new JsonObject { ["id"] = type, ["isMultiValue"] = multi } },
        ["bundle"] = values.Length == 0 ? null : new JsonObject
        {
            ["values"] = new JsonArray([.. values.Select(v => (JsonNode)new JsonObject { ["name"] = v.Name, ["isResolved"] = v.Resolved })]),
        },
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        var path = url.AbsolutePath[(new Uri(Site).AbsolutePath.TrimEnd('/').Length + 1)..];
        var query = HttpUtility.ParseQueryString(url.Query);
        lock (Requests)
        {
            Requests.Add($"{path}?{Uri.UnescapeDataString(url.Query.TrimStart('?'))}");
        }

        if (request.Headers.Authorization?.ToString() != $"Bearer {Token}")
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        await Task.Yield();
        var parts = path.Split('/');
        return parts switch
        {
            ["api", "admin", "projects"] => Page(Projects, query),
            ["api", "admin", "projects", var id, "customFields"] => Page(CustomFields.GetValueOrDefault(id) ?? [], query),
            ["api", "users"] => Page(Users, query),
            ["api", "agiles"] => Page(Agiles, query),
            ["api", "agiles", _, "sprints", var sprint] => Json(Sprints[sprint]),
            ["api", "issues"] => Page(Search(query["query"]!), query),
            ["api", "issues", var id, "activitiesPage"] => ActivitiesPage(id, query["cursor"]),
            ["api", "files", var file] when query["sign"] == "ok" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Files[file]) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    /// <summary>The bits of YouTrack's query language the connector writes: <c>project: {X}</c> and <c>updated: day .. Today</c>.</summary>
    private List<JsonObject> Search(string text)
    {
        var project = text.Split('{', '}')[1];
        var found = Issues.Where(i => i.Project == project).Select(i => i.Issue);
        if (text.Contains("updated: ", StringComparison.Ordinal))
        {
            var day = DateOnly.ParseExact(text.Split("updated: ")[1][..10], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            found = found.Where(i => (long)i["updated"]! >= Ms(from));
        }

        return [.. found];
    }

    private HttpResponseMessage ActivitiesPage(string issue, string? cursor)
    {
        var all = Activities.GetValueOrDefault(issue) ?? [];
        var start = cursor is null ? 0 : int.Parse(cursor, CultureInfo.InvariantCulture);
        var page = all.Skip(start).Take(ActivityPage).Select(a => (JsonNode)a.DeepClone()).ToArray();
        var more = start + ActivityPage < all.Count;
        return Json(new JsonObject
        {
            ["activities"] = new JsonArray(page),
            ["hasAfter"] = more,
            ["afterCursor"] = more ? (start + ActivityPage).ToString(CultureInfo.InvariantCulture) : null,
        });
    }

    private static HttpResponseMessage Page(List<JsonObject> items, System.Collections.Specialized.NameValueCollection query)
    {
        var skip = int.Parse(query["$skip"] ?? "0", CultureInfo.InvariantCulture);
        var top = int.Parse(query["$top"] ?? "42", CultureInfo.InvariantCulture);
        return Json(new JsonArray([.. items.Skip(skip).Take(top).Select(i => (JsonNode)i.DeepClone())]));
    }

    private static HttpResponseMessage Json(JsonNode node) =>
        new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
}
