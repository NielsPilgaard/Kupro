using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kupro.Tests.Infrastructure;

/// <summary>
/// An Azure DevOps collection for connector tests (task 025): serves the REST endpoints the connector calls, in the
/// response shapes of the 7.1 docs, from in-memory projects and work items. Like the real service it rejects a bad PAT
/// with a 203 sign-in page, an API version above <see cref="MaxVersion"/> with a 400 naming the newest it supports,
/// a batch over 200 IDs, and WIQL results over 20,000. Every request is logged for assertions.
/// </summary>
internal sealed partial class FakeAzureDevOps : HttpMessageHandler
{
    public const string Token = "fake-pat";

    public FakeAzureDevOps(string collection = "https://dev.azure.com/acme") => Collection = new Uri(collection);

    public Uri Collection { get; }

    /// <summary>The newest API version served: 7.1 is Services, 6.0 is Server 2020.</summary>
    public string MaxVersion { get; set; } = "7.1";

    public List<FakeProject> Projects { get; } = [];
    public List<FakeWorkItem> Items { get; } = [];
    public List<JsonObject> Fields { get; } = [];
    public Dictionary<string, byte[]> Files { get; } = [];

    /// <summary>The next N requests are answered 429 with <see cref="RetryAfter"/>.</summary>
    public int Throttle { get; set; }

    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>"GET path?query" for every request that passed auth, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Hosts other than the collection's that were called (must stay empty: the PAT goes nowhere else).</summary>
    public List<string> ForeignHosts { get; } = [];

    public HttpClient Client() => new(this);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        if (!url.Authority.Equals(Collection.Authority, StringComparison.OrdinalIgnoreCase))
        {
            lock (ForeignHosts)
            {
                ForeignHosts.Add(url.Authority);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var expected = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + Token));
        if (request.Headers.Authorization?.ToString() != expected)
        {
            return new HttpResponseMessage(HttpStatusCode.NonAuthoritativeInformation)
            {
                Content = new StringContent("<html>Sign in</html>", Encoding.UTF8, "text/html"),
            };
        }

        lock (Requests)
        {
            Requests.Add($"{request.Method} {url.PathAndQuery}");
            if (Throttle > 0)
            {
                Throttle--;
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(RetryAfter);
                return throttled;
            }
        }

        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        var version = query["api-version"] ?? "";
        if (Version.Parse(version.Split('-')[0]) > Version.Parse(MaxVersion))
        {
            return Json(HttpStatusCode.BadRequest, new JsonObject
            {
                ["message"] = $"The requested REST API version of {version} is out of range for this server. The latest REST API version this server supports is {MaxVersion}.",
                ["typeKey"] = "VssVersionOutOfRangeException",
            });
        }

        var path = url.AbsolutePath[Collection.AbsolutePath.TrimEnd('/').Length..].Trim('/');
        var segments = path.Split('/').Select(Uri.UnescapeDataString).ToArray();
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
        return Route(request.Method, segments, query, version, body) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private HttpResponseMessage? Route(HttpMethod method, string[] s, System.Collections.Specialized.NameValueCollection query, string version, JsonNode? body)
    {
        int Int(string name, int fallback) => int.TryParse(query[name], CultureInfo.InvariantCulture, out var value) ? value : fallback;
        FakeProject? Project(string id) => Projects.FirstOrDefault(p => p.Id == id || p.Name.Equals(id, StringComparison.OrdinalIgnoreCase));

        switch (s)
        {
            case ["_apis", "projects"]:
                return List(Projects.Skip(Int("$skip", 0)).Take(Int("$top", 100)).Select(p => new JsonObject
                {
                    ["id"] = p.Id,
                    ["name"] = p.Name,
                    ["state"] = "wellFormed",
                }));
            case ["_apis", "projects", var id] when Project(id) is { } project:
                return Json(HttpStatusCode.OK, new JsonObject
                {
                    ["id"] = project.Id,
                    ["name"] = project.Name,
                    ["capabilities"] = new JsonObject
                    {
                        ["processTemplate"] = new JsonObject { ["templateName"] = project.Process.Name, ["templateTypeId"] = project.Process.TypeId },
                    },
                });
            case ["_apis", "work", "processes", var id]:
                return Projects.Select(p => p.Process).FirstOrDefault(p => p.TypeId == id) is { } process
                    ? Json(HttpStatusCode.OK, new JsonObject { ["typeId"] = process.TypeId, ["name"] = process.Name, ["parentProcessTypeId"] = process.ParentTypeId })
                    : null;
            case [var id, "_apis", "wit", "workitemtypes"] when Project(id) is { } project:
                return List(project.Process.Types.Select(t => new JsonObject
                {
                    ["name"] = t.Name,
                    ["referenceName"] = "Microsoft.VSTS.WorkItemTypes." + t.Name.Replace(" ", "", StringComparison.Ordinal),
                    ["isDisabled"] = false,
                    ["states"] = new JsonArray([.. t.States.Select(st => (JsonNode)new JsonObject { ["name"] = st.Name, ["color"] = "b2b2b2", ["category"] = st.Category })]),
                }));
            case [var id, "_apis", "work", "backlogconfiguration"] when Project(id) is { } project:
                JsonObject Backlog(string name, int rank, Func<FakeType, bool> match) => new()
                {
                    ["name"] = name,
                    ["rank"] = rank,
                    ["workItemTypes"] = new JsonArray([.. project.Process.Types.Where(match).Select(t => (JsonNode)new JsonObject { ["name"] = t.Name })]),
                };
                return Json(HttpStatusCode.OK, new JsonObject
                {
                    ["taskBacklog"] = Backlog("Tasks", 0, t => t.Level == 0),
                    ["requirementBacklog"] = Backlog("Stories", 1, t => t.Level == 1),
                    ["portfolioBacklogs"] = new JsonArray(
                        // Returned out of order on purpose: the connector ranks them itself.
                        Backlog("Epics", 3, t => t.Level == 3),
                        Backlog("Features", 2, t => t.Level == 2)),
                });
            case ["_apis", "wit", "fields"]:
                return List(Fields.Select(f => f.DeepClone().AsObject()));
            case [var id, "_apis", "wit", "classificationnodes", var group] when Project(id) is { } project:
                return Json(HttpStatusCode.OK, Node(group == "Iterations" ? project.Iterations : project.Areas));
            case [var id, "_apis", "wit", "wiql"] when method == HttpMethod.Post && Project(id) is { } project:
                return Wiql(project, body!["query"]!.GetValue<string>(), Int("$top", int.MaxValue));
            case ["_apis", "wit", "workitemsbatch"] when method == HttpMethod.Post:
                var ids = body!["ids"]!.AsArray().Select(n => n!.GetValue<int>()).ToList();
                if (ids.Count > 200 || body["$expand"]?.GetValue<string>() != "Relations")
                {
                    return Json(HttpStatusCode.BadRequest, new JsonObject { ["message"] = "VS403474: batch over 200 or no $expand." });
                }

                var byId = Items.ToDictionary(i => i.Id);
                return List(ids.Select(i => byId.TryGetValue(i, out var item) ? item.ToJson(Collection) : null));
            case ["_apis", "wit", "workItems", var id, "updates"] when Find(id) is { } item:
                return List(item.Updates.Skip(Int("$skip", 0)).Take(Int("$top", 200)).Select(u => u.DeepClone().AsObject()));
            case [var project, "_apis", "wit", "workItems", var id, "comments"] when Find(id) is { } item && Project(project) is not null:
                if (!version.Contains("-preview", StringComparison.Ordinal))
                {
                    return Json(HttpStatusCode.BadRequest, new JsonObject { ["message"] = "The comments API requires -preview." });
                }

                var start = Int("continuationToken", 0);
                var top = Int("$top", 200);
                var page = item.Comments.Skip(start).Take(top).Select(c => (JsonNode)c.DeepClone()).ToArray();
                return Json(HttpStatusCode.OK, new JsonObject
                {
                    ["totalCount"] = item.Comments.Count,
                    ["count"] = page.Length,
                    ["comments"] = new JsonArray(page),
                    ["continuationToken"] = start + top < item.Comments.Count ? (start + top).ToString(CultureInfo.InvariantCulture) : null,
                });
            case [_, "_apis", "wit", "attachments", var file] when query["download"] == "true" && Files.TryGetValue(file, out var bytes):
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            default:
                return null;
        }
    }

    private FakeWorkItem? Find(string id) =>
        int.TryParse(id, CultureInfo.InvariantCulture, out var number) ? Items.FirstOrDefault(i => i.Id == number) : null;

    private HttpResponseMessage Wiql(FakeProject project, string query, int top)
    {
        var after = int.Parse(IdAbove().Match(query).Groups[1].Value, CultureInfo.InvariantCulture);
        var since = ChangedSince().Match(query) is { Success: true } match
            ? DateTimeOffset.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : (DateTimeOffset?)null;
        var ids = Items
            .Where(i => i.ProjectId == project.Id && i.Id > after && (since is null || i.Changed >= since))
            .OrderBy(i => i.Id)
            .Select(i => i.Id)
            .ToList();
        if (ids.Count > 20_000 && top > 20_000)
        {
            return Json(HttpStatusCode.BadRequest, new JsonObject { ["message"] = "VS402337: The number of work items returned exceeds the size limit of 20000." });
        }

        return Json(HttpStatusCode.OK, new JsonObject
        {
            ["queryType"] = "flat",
            ["workItems"] = new JsonArray([.. ids.Take(top).Select(id => (JsonNode)new JsonObject { ["id"] = id, ["url"] = $"{Collection}/_apis/wit/workItems/{id}" })]),
        });
    }

    private static JsonObject Node(FakeNode node)
    {
        var json = new JsonObject { ["id"] = node.Id, ["identifier"] = Guid.NewGuid().ToString(), ["name"] = node.Name, ["hasChildren"] = node.Children.Count > 0 };
        if (node.Start is not null || node.Finish is not null)
        {
            json["attributes"] = new JsonObject { ["startDate"] = node.Start?.ToString("O"), ["finishDate"] = node.Finish?.ToString("O") };
        }

        if (node.Children.Count > 0)
        {
            json["children"] = new JsonArray([.. node.Children.Select(c => (JsonNode)Node(c))]);
        }

        return json;
    }

    private static HttpResponseMessage List(IEnumerable<JsonObject?> values)
    {
        var array = new JsonArray([.. values.Select(v => (JsonNode?)v)]);
        return Json(HttpStatusCode.OK, new JsonObject { ["count"] = array.Count, ["value"] = array });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    [GeneratedRegex(@"\[System\.Id\] > (\d+)")]
    private static partial Regex IdAbove();

    [GeneratedRegex(@"\[System\.ChangedDate\] >= '([^']+)'")]
    private static partial Regex ChangedSince();

    // ---- Building a collection ----

    public static JsonObject Identity(string id, string name, string unique) => new()
    {
        ["displayName"] = name,
        ["id"] = id,
        ["uniqueName"] = unique,
        ["url"] = $"https://spsprodweu5.vssps.visualstudio.com/_apis/Identities/{id}",
    };

    public static JsonObject Field(string reference, string name, string type, bool picklist = false, bool readOnly = false) => new()
    {
        ["name"] = name,
        ["referenceName"] = reference,
        ["type"] = type,
        ["isPicklist"] = picklist,
        ["readOnly"] = readOnly,
        ["isIdentity"] = type == "identity",
        ["usage"] = "workItem",
    };

    /// <summary>A few fields of each kind, plus ones the connector must leave out (system, bookkeeping, handled).</summary>
    public static IEnumerable<JsonObject> StandardFields() =>
    [
        Field("System.Title", "Title", "string"),
        Field("System.State", "State", "string"),
        Field("System.Rev", "Rev", "integer", readOnly: true),
        Field("System.AreaPath", "Area Path", "treePath"),
        Field("System.IterationPath", "Iteration Path", "treePath"),
        Field("Microsoft.VSTS.Common.Priority", "Priority", "integer"),
        Field("Microsoft.VSTS.Common.Severity", "Severity", "string", picklist: true),
        Field("Microsoft.VSTS.Common.ValueArea", "Value Area", "string", picklist: true),
        Field("Microsoft.VSTS.Common.StackRank", "Stack Rank", "double"),
        Field("Microsoft.VSTS.Common.ClosedDate", "Closed Date", "dateTime"),
        Field("Microsoft.VSTS.Scheduling.StoryPoints", "Story Points", "double"),
        Field("Microsoft.VSTS.Scheduling.TargetDate", "Target Date", "dateTime"),
        Field("Microsoft.VSTS.TCM.Steps", "Steps", "html"),
        Field("WEF_6CB513B6E70E43499D9FC94E5BBFB784_Kanban.Column", "Board Column", "string"),
        Field("Custom.Team", "Team", "string", picklist: true),
        Field("Custom.Reviewer", "Reviewer", "identity"),
        Field("Custom.GoLive", "Go-live", "dateTime"),
        Field("Custom.Budget", "Budget", "double"),
        Field("Custom.Notes", "Notes", "plainText"),
    ];

    public FakeProject AddProject(string name, FakeProcess process, int number)
    {
        var project = new FakeProject($"00000000-0000-0000-0000-{number:D12}", name, process);
        Projects.Add(project);
        return project;
    }

    public FakeWorkItem AddItem(FakeProject project, int id, string type, string state, string title, DateTimeOffset created)
    {
        var item = new FakeWorkItem(id, project, created) { Type = type };
        item.Fields["System.WorkItemType"] = type;
        item.Fields["System.State"] = state;
        item.Fields["System.Title"] = title;
        Items.Add(item);
        return item;
    }
}

/// <summary>A process: its type ID (fixed for the four system processes), parent for inherited ones, and its types.</summary>
internal sealed record FakeProcess(string TypeId, string Name, string? ParentTypeId, IReadOnlyList<FakeType> Types)
{
    private static readonly (string, string)[] AgileStates =
        [("New", "Proposed"), ("Active", "InProgress"), ("Resolved", "Resolved"), ("Closed", "Completed"), ("Removed", "Removed")];

    public static readonly FakeProcess Basic = new("b8a3a935-7e91-48b8-a94c-606d37c3e9f2", "Basic", null,
    [
        new("Epic", 3, [("To Do", "Proposed"), ("Doing", "InProgress"), ("Done", "Completed")]),
        new("Issue", 1, [("To Do", "Proposed"), ("Doing", "InProgress"), ("Done", "Completed")]),
        new("Task", 0, [("To Do", "Proposed"), ("Doing", "InProgress"), ("Done", "Completed")]),
    ]);

    public static readonly FakeProcess Agile = new("adcc42ab-9882-485e-a3ed-7678f01f66bc", "Agile", null,
    [
        new("Epic", 3, AgileStates),
        new("Feature", 2, AgileStates),
        new("User Story", 1, AgileStates),
        new("Bug", 1, AgileStates),
        new("Task", 0, [("New", "Proposed"), ("Active", "InProgress"), ("Closed", "Completed"), ("Removed", "Removed")]),
        new("Issue", null, [("Active", "InProgress"), ("Closed", "Completed")]),
        new("Test Case", null, [("Design", "Proposed"), ("Ready", "InProgress"), ("Closed", "Completed")]),
    ]);

    public static readonly FakeProcess Scrum = new("6b724908-ef14-45cf-84f8-768b5384da45", "Scrum", null,
    [
        new("Epic", 3, [("New", "Proposed"), ("In Progress", "InProgress"), ("Done", "Completed"), ("Removed", "Removed")]),
        new("Feature", 2, [("New", "Proposed"), ("In Progress", "InProgress"), ("Done", "Completed"), ("Removed", "Removed")]),
        new("Product Backlog Item", 1, [("New", "Proposed"), ("Approved", "Proposed"), ("Committed", "InProgress"), ("Done", "Completed"), ("Removed", "Removed")]),
        new("Task", 0, [("To Do", "Proposed"), ("In Progress", "InProgress"), ("Done", "Completed"), ("Removed", "Removed")]),
        new("Impediment", null, [("Open", "Proposed"), ("Closed", "Completed")]),
    ]);

    public static readonly FakeProcess Cmmi = new("27450541-8e31-4150-9947-dc59f998fc01", "CMMI", null,
    [
        new("Epic", 3, [("Proposed", "Proposed"), ("Active", "InProgress"), ("Resolved", "Resolved"), ("Closed", "Completed")]),
        new("Requirement", 1, [("Proposed", "Proposed"), ("Active", "InProgress"), ("Resolved", "Resolved"), ("Closed", "Completed")]),
        new("Task", 0, [("Proposed", "Proposed"), ("Active", "InProgress"), ("Resolved", "Resolved"), ("Closed", "Completed")]),
        new("Risk", null, [("Proposed", "Proposed"), ("Active", "InProgress"), ("Closed", "Completed")]),
    ]);

    /// <summary>An inherited process: its own type ID, the parent's types plus one custom type.</summary>
    public static FakeProcess Inherited(string name, FakeProcess parent, string customType) =>
        new("3c1f0a52-7d55-4e5b-9d6e-1b2b8f0e4a11", name, parent.TypeId,
            [.. parent.Types, new FakeType(customType, null, [("Open", "Proposed"), ("Investigating", "InProgress"), ("Closed", "Completed")])]);
}

/// <summary>A work item type; <see cref="Level"/> is its backlog (0 tasks, 1 requirements, 2+ portfolio, null none).</summary>
internal sealed record FakeType(string Name, int? Level, IReadOnlyList<(string Name, string Category)> States);

internal sealed record FakeProject(string Id, string Name, FakeProcess Process)
{
    public FakeNode Iterations { get; } = new(1, Name);
    public FakeNode Areas { get; } = new(2, Name);
}

internal sealed record FakeNode(int Id, string Name, DateTimeOffset? Start = null, DateTimeOffset? Finish = null)
{
    public List<FakeNode> Children { get; } = [];

    public FakeNode Add(int id, string name, DateTimeOffset? start = null, DateTimeOffset? finish = null)
    {
        var child = new FakeNode(id, name, start, finish);
        Children.Add(child);
        return this;
    }
}

/// <summary>
/// A work item: its current fields (system ones filled in), relations, updates and comments. <see cref="Update"/>
/// appends an update in the shape of <c>wit/workItems/{id}/updates</c> and applies it to the fields.
/// </summary>
internal sealed class FakeWorkItem
{
    private readonly FakeProject _project;

    public FakeWorkItem(int id, FakeProject project, DateTimeOffset created)
    {
        Id = id;
        _project = project;
        Changed = created;
        Fields["System.Id"] = id;
        Fields["System.TeamProject"] = project.Name;
        Fields["System.AreaPath"] = project.Name;
        Fields["System.IterationPath"] = project.Name;
        Fields["System.CreatedDate"] = created.ToString("O");
        Fields["System.CreatedBy"] = FakeAzureDevOps.Identity("u-jonas", "Jonas Holm", "jonas@acme.example");
        Fields["System.CommentCount"] = 0;
        Fields["Microsoft.VSTS.Common.StackRank"] = 1999.5;
    }

    public int Id { get; }
    public string ProjectId => _project.Id;
    public required string Type { get; init; }
    public DateTimeOffset Changed { get; private set; }
    public JsonObject Fields { get; } = [];
    public List<JsonObject> Relations { get; } = [];
    public List<JsonObject> Updates { get; } = [];
    public List<JsonObject> Comments { get; } = [];

    public FakeWorkItem Set(string field, JsonNode? value)
    {
        Fields[field] = value;
        return this;
    }

    public FakeWorkItem Link(string rel, int other, string name)
    {
        Relations.Add(Relation(rel, $"https://dev.azure.com/acme/_apis/wit/workItems/{other}", name));
        return this;
    }

    public static JsonObject Relation(string rel, string url, string name, JsonObject? attributes = null)
    {
        attributes ??= [];
        attributes["name"] = name;
        attributes["isLocked"] = false;
        return new JsonObject { ["rel"] = rel, ["url"] = url, ["attributes"] = attributes };
    }

    /// <summary>The creation update (revision 1), with every field as a new value.</summary>
    public FakeWorkItem Created()
    {
        var fields = new JsonObject();
        foreach (var (name, value) in Fields)
        {
            fields[name] = new JsonObject { ["newValue"] = value?.DeepClone() };
        }

        Updates.Add(new JsonObject
        {
            ["id"] = 1,
            ["workItemId"] = Id,
            ["rev"] = 1,
            ["revisedBy"] = Fields["System.CreatedBy"]!.DeepClone(),
            ["revisedDate"] = "9999-01-01T00:00:00Z",
            ["fields"] = fields,
        });
        return this;
    }

    /// <summary>A later update by <paramref name="by"/> at <paramref name="at"/>: field (old, new) pairs and link changes.</summary>
    public FakeWorkItem Update(DateTimeOffset at, JsonObject by, (string Field, JsonNode? Old, JsonNode? New)[] changes, JsonObject[]? added = null, JsonObject[]? removed = null)
    {
        if (Updates.Count == 0)
        {
            Created();
        }

        var rev = Updates.Count + 1;
        var fields = new JsonObject
        {
            ["System.Rev"] = new JsonObject { ["oldValue"] = rev - 1, ["newValue"] = rev },
            ["System.ChangedDate"] = new JsonObject { ["oldValue"] = Changed.ToString("O"), ["newValue"] = at.ToString("O") },
            ["System.ChangedBy"] = new JsonObject { ["newValue"] = by.DeepClone() },
        };
        foreach (var (field, old, @new) in changes)
        {
            fields[field] = new JsonObject { ["oldValue"] = old?.DeepClone(), ["newValue"] = @new?.DeepClone() };
            Fields[field] = @new?.DeepClone();
        }

        var update = new JsonObject
        {
            ["id"] = rev,
            ["workItemId"] = Id,
            ["rev"] = rev,
            ["revisedBy"] = by.DeepClone(),
            ["revisedDate"] = "9999-01-01T00:00:00Z",
            ["fields"] = fields,
        };
        if (added is not null || removed is not null)
        {
            update["relations"] = new JsonObject
            {
                ["added"] = new JsonArray([.. (added ?? []).Select(r => (JsonNode)r.DeepClone())]),
                ["removed"] = new JsonArray([.. (removed ?? []).Select(r => (JsonNode)r.DeepClone())]),
            };
        }

        Updates.Add(update);
        Changed = at;
        return this;
    }

    public FakeWorkItem Comment(int id, string text, JsonObject by, DateTimeOffset at, string format = "html")
    {
        Comments.Add(new JsonObject
        {
            ["workItemId"] = Id,
            ["id"] = id,
            ["version"] = 1,
            ["text"] = text,
            ["format"] = format,
            ["createdBy"] = by.DeepClone(),
            ["createdDate"] = at.ToString("O"),
            ["modifiedDate"] = at.ToString("O"),
        });
        return this;
    }

    public JsonObject ToJson(Uri collection)
    {
        var fields = Fields.DeepClone().AsObject();
        fields["System.Rev"] = Math.Max(1, Updates.Count);
        fields["System.ChangedDate"] = Changed.ToString("O");
        fields["System.CommentCount"] = Comments.Count;
        var json = new JsonObject
        {
            ["id"] = Id,
            ["rev"] = Math.Max(1, Updates.Count),
            ["fields"] = fields,
            ["url"] = $"{collection}/_apis/wit/workItems/{Id}",
        };
        if (Relations.Count > 0)
        {
            json["relations"] = new JsonArray([.. Relations.Select(r => (JsonNode)r.DeepClone())]);
        }

        return json;
    }
}
