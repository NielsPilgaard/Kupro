using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Kupro.Import.Snapshot;

namespace Kupro.Import.GitHub;

/// <summary>
/// GitHub Projects (v2) through GraphQL (task 027): the boards each repository owner has, and for the one the owner
/// picks, its Status option, Iteration and effort number per issue. The connector folds these into its items.
/// </summary>
internal static class GitHubBoards
{
    private const string BoardsQuery = """
        query($login: String!, $after: String) {
          repositoryOwner(login: $login) {
            ... on ProjectV2Owner {
              projectsV2(first: 50, after: $after) {
                pageInfo { hasNextPage endCursor }
                nodes { id title url field(name: "Status") { ... on ProjectV2SingleSelectField { options { id name } } } }
              }
            }
          }
        }
        """;

    private const string FieldsQuery = """
        query($board: ID!) {
          node(id: $board) {
            ... on ProjectV2 {
              fields(first: 50) {
                nodes {
                  ... on ProjectV2Field { id name dataType }
                  ... on ProjectV2SingleSelectField { id name dataType }
                  ... on ProjectV2IterationField {
                    id name dataType
                    configuration { iterations { id title startDate duration } completedIterations { id title startDate duration } }
                  }
                }
              }
            }
          }
        }
        """;

    private const string ItemsQuery = """
        query($board: ID!, $after: String) {
          node(id: $board) {
            ... on ProjectV2 {
              items(first: 100, after: $after) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  isArchived
                  updatedAt
                  content { ... on Issue { number repository { nameWithOwner } } }
                  fieldValues(first: 50) {
                    nodes {
                      ... on ProjectV2ItemFieldSingleSelectValue { optionId field { ... on ProjectV2FieldCommon { id } } }
                      ... on ProjectV2ItemFieldIterationValue { iterationId field { ... on ProjectV2FieldCommon { id } } }
                      ... on ProjectV2ItemFieldNumberValue { number field { ... on ProjectV2FieldCommon { id } } }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    /// <summary>Number fields that hold effort, by name; with none named so, a board's only number field.</summary>
    private static readonly string[] EffortNames = ["estimate", "point", "size", "effort"];

    /// <summary>
    /// The boards of each owner, with their Status options as statuses scoped to the board. An owner whose boards the
    /// token may not read (a fine-grained token without Projects, a server without Projects (v2)) has none.
    /// </summary>
    public static async Task<(List<SourceBoard> Boards, List<SourceStatus> Statuses)> DescribeAsync(GitHubClient client, IEnumerable<string> owners, CancellationToken ct)
    {
        var boards = new List<SourceBoard>();
        var statuses = new List<SourceStatus>();
        foreach (var owner in owners)
        {
            string? after = null;
            do
            {
                JsonObject? page;
                try
                {
                    page = (await client.QueryAsync(BoardsQuery, new JsonObject { ["login"] = owner, ["after"] = after }, ct)).Data["repositoryOwner"]?["projectsV2"] as JsonObject;
                }
                catch (SourceAccessException e) when (e.Status != HttpStatusCode.Unauthorized)
                {
                    // Boards are extra: a server or proxy that refuses GraphQL still leaves the issues readable.
                    page = null;
                }

                foreach (var node in page?["nodes"]?.AsArray().OfType<JsonObject>() ?? [])
                {
                    var id = (string)node["id"]!;
                    boards.Add(new SourceBoard { Id = id, Name = (string?)node["title"] ?? id, Owner = owner, Url = (string?)node["url"] });
                    statuses.AddRange(node["field"]?["options"]?.AsArray().OfType<JsonObject>().Select(o => new SourceStatus
                    {
                        Id = ScopedId(id, (string)o["id"]!),
                        Name = (string?)o["name"] ?? "",
                        Board = id,
                    }) ?? []);
                }

                after = page?["pageInfo"]?["hasNextPage"]?.GetValue<bool>() == true ? (string?)page["pageInfo"]!["endCursor"] : null;
            }
            while (after is not null);
        }

        return (boards, statuses);
    }

    /// <summary>The picked board's iterations as sprints, and its values per issue, keyed <c>owner/repo#89</c>.</summary>
    public static async Task<BoardRead> ReadAsync(GitHubClient client, SourceBoard board, CancellationToken ct)
    {
        var (data, errors) = await client.QueryAsync(FieldsQuery, new JsonObject { ["board"] = board.Id }, ct);
        var fields = data["node"]?["fields"]?["nodes"]?.AsArray().OfType<JsonObject>().Where(f => f["id"] is not null).ToList();
        if (fields is null)
        {
            throw Unreadable(board, errors);
        }

        string? Named(string type, Func<string, bool> name) =>
            fields.Where(f => (string?)f["dataType"] == type && name((string?)f["name"] ?? "")).Select(f => (string?)f["id"]).FirstOrDefault();
        var numbers = fields.Count(f => (string?)f["dataType"] == "NUMBER");
        var status = Named("SINGLE_SELECT", n => n.Equals("Status", StringComparison.OrdinalIgnoreCase));
        var iteration = fields.FirstOrDefault(f => (string?)f["dataType"] == "ITERATION");
        var effort = Named("NUMBER", n => EffortNames.Any(e => n.Contains(e, StringComparison.OrdinalIgnoreCase)))
            ?? (numbers == 1 ? Named("NUMBER", _ => true) : null);

        var sprints = new List<SourceSprint>();
        if (iteration?["configuration"] is JsonObject configuration)
        {
            foreach (var (list, closed) in new[] { ("iterations", false), ("completedIterations", true) })
            {
                foreach (var i in configuration[list]?.AsArray().OfType<JsonObject>() ?? [])
                {
                    var start = DateOnly.TryParse((string?)i["startDate"], CultureInfo.InvariantCulture, out var s) ? s : (DateOnly?)null;
                    sprints.Add(new SourceSprint
                    {
                        Id = ScopedId(board.Id, (string)i["id"]!),
                        Name = (string?)i["title"] ?? "",
                        Start = start,
                        End = start?.AddDays(Math.Max(1, (int?)i["duration"] ?? 1) - 1),
                        Closed = closed,
                    });
                }
            }
        }

        var values = new Dictionary<string, BoardValue>(StringComparer.OrdinalIgnoreCase);
        string? after = null;
        do
        {
            (data, errors) = await client.QueryAsync(ItemsQuery, new JsonObject { ["board"] = board.Id, ["after"] = after }, ct);
            var page = data["node"]?["items"] as JsonObject ?? throw Unreadable(board, errors);
            foreach (var node in page["nodes"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                // An archived item's values are stale; drafts and pull requests have no issue content.
                if (node["isArchived"]?.GetValue<bool>() == true
                    || node["content"]?["repository"]?["nameWithOwner"] is not JsonValue repo || node["content"]?["number"] is not JsonValue number)
                {
                    continue;
                }

                string? option = null, sprint = null;
                decimal? points = null;
                foreach (var value in node["fieldValues"]?["nodes"]?.AsArray().OfType<JsonObject>() ?? [])
                {
                    var field = (string?)value["field"]?["id"];
                    if (field is null)
                    {
                        continue;
                    }

                    if (field == status && (string?)value["optionId"] is { } o)
                    {
                        option = ScopedId(board.Id, o);
                    }
                    else if (field == (string?)iteration?["id"] && (string?)value["iterationId"] is { } it)
                    {
                        sprint = ScopedId(board.Id, it);
                    }
                    else if (field == effort && value["number"] is JsonValue n)
                    {
                        points = n.GetValue<decimal>();
                    }
                }

                values[$"{(string)repo!}#{(int)number}"] = new BoardValue(option, sprint, points, node["updatedAt"]?.GetValue<DateTimeOffset>());
            }

            after = page["pageInfo"]?["hasNextPage"]?.GetValue<bool>() == true ? (string?)page["pageInfo"]!["endCursor"] : null;
        }
        while (after is not null);

        return new BoardRead(sprints, values);
    }

    /// <summary>A board's option or iteration ID: those are unique within a field, not across boards.</summary>
    private static string ScopedId(string board, string id) => $"{board}:{id}";

    private static SourceAccessException Unreadable(SourceBoard board, JsonArray errors) =>
        errors.Any(e => (string?)e?["type"] == "NOT_FOUND")
            ? new($"The GitHub project {board.Name} no longer exists. Pick another one, or none.", HttpStatusCode.NotFound)
            : new($"GitHub won't show the project {board.Name} to this token ({errors.Select(e => (string?)e?["message"]).FirstOrDefault() ?? "no answer"}). "
                + "An organisation's projects need Projects (read) on the organisation; a user's projects need a classic token with read:project.",
                HttpStatusCode.Forbidden);
}

/// <summary>One issue's values on the picked board: a status and sprint ID as the snapshot names them, and its effort.</summary>
internal sealed record BoardValue(string? Status, string? Sprint, decimal? Points, DateTimeOffset? Updated);

internal sealed record BoardRead(List<SourceSprint> Sprints, Dictionary<string, BoardValue> Values);
