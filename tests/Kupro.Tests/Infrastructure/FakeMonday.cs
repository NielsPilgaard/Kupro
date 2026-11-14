using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Kupro.Tests.Infrastructure;

/// <summary>
/// Monday's GraphQL API for connector tests (task 027): each query the connector sends is recognised by its root
/// field and answered from in-memory boards, users, items (one per page, so the cursor is followed), activity logs and
/// assets. A raw token in <c>Authorization</c>, as Monday takes it.
/// </summary>
internal sealed class FakeMonday : HttpMessageHandler
{
    public const string Token = "monday-token";
    public const string FileHost = "https://files.monday.example";

    public List<JsonObject> Boards { get; } = [];
    public List<JsonObject> Users { get; } = [];
    public Dictionary<string, List<JsonObject>> Items { get; } = [];
    public Dictionary<string, List<JsonObject>> Logs { get; } = [];
    public Dictionary<string, byte[]> Files { get; } = [];
    public List<string> Queries { get; } = [];

    /// <summary>The next query is refused for complexity, once.</summary>
    public bool ComplexityOnce { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsoluteUri.StartsWith(FileHost, StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Files[request.RequestUri.Segments[^1]]) };
        }

        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
        var query = (string)body["query"]!;
        var variables = body["variables"]!;
        lock (Queries)
        {
            Queries.Add(query);
        }

        if (request.Headers.TryGetValues("Authorization", out var auth) && auth.Single() != Token)
        {
            return Json(new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = "Not Authenticated", ["extensions"] = new JsonObject { ["code"] = "USER_UNAUTHORIZED" } }) });
        }

        if (ComplexityOnce)
        {
            ComplexityOnce = false;
            return Json(new JsonObject
            {
                ["errors"] = new JsonArray(new JsonObject
                {
                    ["message"] = "Complexity budget exhausted",
                    ["extensions"] = new JsonObject { ["code"] = "COMPLEXITY_BUDGET_EXHAUSTED", ["retry_in_seconds"] = 7 },
                }),
            });
        }

        JsonObject data;
        if (query.Contains("me {", StringComparison.Ordinal))
        {
            data = new JsonObject { ["me"] = new JsonObject { ["account"] = new JsonObject { ["slug"] = "acme" } } };
        }
        else if (query.Contains("boards(limit:", StringComparison.Ordinal))
        {
            data = new JsonObject { ["boards"] = Page(Boards, (int)variables["page"]!) };
        }
        else if (query.Contains("users(limit:", StringComparison.Ordinal))
        {
            data = new JsonObject { ["users"] = Page(Users, (int)variables["page"]!) };
        }
        else if (query.Contains("next_items_page", StringComparison.Ordinal))
        {
            var (board, index) = Cursor((string)variables["cursor"]!);
            data = new JsonObject { ["next_items_page"] = ItemsPage(board, index) };
        }
        else if (query.Contains("items_page", StringComparison.Ordinal))
        {
            var board = (string)variables["board"]![0]!;
            data = new JsonObject { ["boards"] = new JsonArray(new JsonObject { ["items_page"] = ItemsPage(board, 0) }) };
        }
        else if (query.Contains("items(ids:", StringComparison.Ordinal))
        {
            var id = (string)variables["ids"]![0]!;
            var item = Items.Values.SelectMany(i => i).SelectMany(i => (i["subitems"]?.AsArray().OfType<JsonObject>() ?? []).Prepend(i)).Single(i => (string)i["id"]! == id);
            var page = (int)variables["page"]!;
            data = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["updates"] = Page(item["updates"]?.AsArray().OfType<JsonObject>().ToList() ?? [], page) }) };
        }
        else if (query.Contains("activity_logs", StringComparison.Ordinal))
        {
            var board = (string)variables["board"]![0]!;
            data = new JsonObject { ["boards"] = new JsonArray(new JsonObject { ["activity_logs"] = Page(Logs.GetValueOrDefault(board) ?? [], (int)variables["page"]!) }) };
        }
        else if (query.Contains("assets(ids:", StringComparison.Ordinal))
        {
            var id = (string)variables["ids"]![0]!;
            data = new JsonObject { ["assets"] = new JsonArray(new JsonObject { ["id"] = id, ["public_url"] = $"{FileHost}/signed/{id}" }) };
        }
        else
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        return Json(new JsonObject { ["data"] = data });
    }

    private JsonObject ItemsPage(string board, int index)
    {
        var items = Items.GetValueOrDefault(board) ?? [];
        return new JsonObject
        {
            ["cursor"] = index + 1 < items.Count ? $"{board}|{index + 1}" : null,
            ["items"] = new JsonArray([.. items.Skip(index).Take(1).Select(i => (JsonNode)FirstUpdates(i))]),
        };
    }

    /// <summary>Like Monday, an items page carries only the first 100 updates of each item and subitem.</summary>
    private static JsonObject FirstUpdates(JsonObject item)
    {
        var copy = item.DeepClone().AsObject();
        foreach (var row in (copy["subitems"]?.AsArray().OfType<JsonObject>() ?? []).Prepend(copy))
        {
            if (row["updates"] is JsonArray updates)
            {
                row["updates"] = new JsonArray([.. updates.Take(100).Select(u => u!.DeepClone())]);
            }
        }

        return copy;
    }

    private static (string Board, int Index) Cursor(string cursor) => (cursor.Split('|')[0], int.Parse(cursor.Split('|')[1], System.Globalization.CultureInfo.InvariantCulture));

    private static JsonArray Page(List<JsonObject> items, int page) =>
        new([.. items.Skip((page - 1) * 100).Take(100).Select(i => (JsonNode)i.DeepClone())]);

    private static HttpResponseMessage Json(JsonNode node) =>
        new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
}
