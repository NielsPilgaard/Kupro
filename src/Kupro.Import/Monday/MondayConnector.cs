using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kupro.Import.Snapshot;

namespace Kupro.Import.Monday;

/// <summary>
/// Monday (task 027, ADR-019): boards as projects, groups as items of their own (Epics by default; the mapping can
/// skip them or use the <see cref="GroupField"/> as labels), items with their subitems, updates and replies as
/// comments, files, and the board's activity log as changes. Monday's columns have no fixed meaning, so per board the
/// status column named so (else the first) is the status, likewise the people column the assignee, a timeline the dates
/// (a date column the due date when the timeline is empty), a numbers column named for an estimate or points the
/// effort, and tags and dropdowns the labels. The owner can pick other columns (<see cref="ReadScope.Columns"/>), and is
/// asked where two columns fit and neither is named. Every other column is a field. This class only reads.
/// </summary>
public sealed class MondayConnector : ISourceConnector
{
    public const string ItemType = "item";
    public const string SubitemType = "subitem";
    public const string GroupType = "group";

    /// <summary>The item's group title, for a mapping that makes groups labels instead of Epics.</summary>
    public const string GroupField = "group";

    /// <summary>Status of an item with no label set in its status column (or no status column).</summary>
    public const string NoStatus = "(none)";

    private const int PageSize = 100;

    private const string BoardsQuery = """
        query ($page: Int!) { boards(limit: 100, page: $page, state: all, order_by: created_at) {
          id name state type columns { id title type settings_str } groups { id title } } }
        """;

    private const string UsersQuery = "query ($page: Int!) { users(limit: 100, page: $page) { id name email is_guest enabled } }";
    private const string AccountQuery = "query { me { account { slug } } }";

    private const string ItemFields = """
        id name created_at updated_at state creator_id group { id }
        column_values { id type text value }
        subitems { id name created_at updated_at state creator_id board { id } column_values { id type text value }
          updates(limit: 100) { id body created_at updated_at creator_id }
          assets { id name file_size created_at uploaded_by { id } } }
        updates(limit: 100) { id body created_at updated_at creator_id replies { id body created_at creator_id } }
        assets { id name file_size created_at uploaded_by { id } }
        """;

    private const string FirstPageQuery = "query ($board: [ID!], $limit: Int!) { boards(ids: $board) { items_page(limit: $limit) { cursor items { " + ItemFields + " } } } }";
    private const string NextPageQuery = "query ($cursor: String!, $limit: Int!) { next_items_page(cursor: $cursor, limit: $limit) { cursor items { " + ItemFields + " } } }";
    private const string LogQuery = "query ($board: [ID!], $page: Int!) { boards(ids: $board) { activity_logs(limit: 100, page: $page) { id event data user_id created_at } } }";
    private const string LogSinceQuery = "query ($board: [ID!], $page: Int!, $from: ISO8601DateTime) { boards(ids: $board) { activity_logs(limit: 100, page: $page, from: $from) { id event data user_id created_at } } }";
    private const string UpdatesQuery = "query ($ids: [ID!], $page: Int!) { items(ids: $ids) { updates(limit: 100, page: $page) { id body created_at updated_at creator_id replies { id body created_at creator_id } } } }";
    private const string AssetQuery = "query ($ids: [ID!]!) { assets(ids: $ids) { id public_url } }";

    /// <summary>Columns that are the item itself or Monday's bookkeeping, never fields.</summary>
    private static readonly HashSet<string> NotFields = new(StringComparer.Ordinal)
    {
        "name", "subtasks", "item_id", "creation_log", "last_updated", "button", "board_relation", "mirror", "formula", "doc", "file", "dependency",
    };

    private readonly MondayClient _client;
    private readonly TimeProvider _time;
    private Catalog? _catalog;

    /// <param name="token">A personal API token (Profile → Developers → My access tokens).</param>
    public MondayConnector(HttpClient http, string token, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _client = new MondayClient(http, token, _time);
    }

    internal MondayClient Client => _client;

    public async Task<SourceDescription> DescribeAsync(CancellationToken ct) => (await CatalogAsync(ct)).Description;

    public async IAsyncEnumerable<ISnapshotRecord> ReadAsync(ReadScope scope, string? cursor, [EnumeratorCancellation] CancellationToken ct)
    {
        // The next delta starts when this read did: anything changed while it ran is read again, nothing is missed.
        var started = _time.GetUtcNow();
        var catalog = await CatalogAsync(ct);
        var since = DeltaCursor.Since(cursor);
        var boards = scope.Projects.Count == 0
            ? catalog.Boards.Where(b => b.State == "active").ToList()
            : [.. scope.Projects.Select(id => catalog.Boards.FirstOrDefault(b => b.Id == id)
                ?? throw new SourceAccessException($"Monday has no board {id} that this token can see.", HttpStatusCode.NotFound))];
        boards = [.. boards.Select(b => b.Picked(scope.Columns.GetValueOrDefault(b.Id)))];

        foreach (var board in boards)
        {
            // A group has no dates of its own, so every read lists them all: a group added since the last read is
            // there for the items in it.
            foreach (var group in board.Groups)
            {
                yield return new SourceItem
                {
                    Id = GroupId(board, group.Id),
                    Project = board.Id,
                    Type = GroupType,
                    Status = NoStatus,
                    Title = group.Title,
                    Url = $"{catalog.Web}/boards/{board.Id}",
                    Created = catalog.ReadAt,
                    Updated = catalog.ReadAt,
                };
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var item in ItemsAsync(board.Id, ct))
            {
                var updated = Time(item["updated_at"]) ?? catalog.ReadAt;
                var records = new List<ISnapshotRecord>();
                var row = Item(catalog, board, item, parent: null, out var resolvedItem);
                if (scope.ResolvedAfter is { } after && resolvedItem < after)
                {
                    // Left out with its subitems, which would otherwise point at an item that isn't there.
                    continue;
                }

                if (since is null || updated >= since)
                {
                    seen.Add(row.Id);
                    records.Add(row);
                    await AllUpdatesAsync(item, ct);
                    records.AddRange(Comments(row.Id, item));
                    records.AddRange(Files(row.Id, item));
                }

                foreach (var sub in item["subitems"]?.AsArray().OfType<JsonObject>() ?? [])
                {
                    var subUpdated = Time(sub["updated_at"]) ?? updated;
                    if (since is not null && subUpdated < since)
                    {
                        continue;
                    }

                    var subBoard = catalog.Subboards.GetValueOrDefault((string?)sub["board"]?["id"] ?? "") ?? board;
                    var subRow = Item(catalog, subBoard, sub, row.Id, out _) with { Project = board.Id };
                    seen.Add(subRow.Id);
                    records.Add(subRow);
                    await AllUpdatesAsync(sub, ct);
                    records.AddRange(Comments(subRow.Id, sub));
                    records.AddRange(Files(subRow.Id, sub));
                }

                foreach (var record in records)
                {
                    yield return record;
                }
            }

            await foreach (var change in ChangesAsync(board, seen, since, ct))
            {
                yield return change;
            }
        }

        yield return new ReadCursor(DeltaCursor.At(started));
    }

    /// <summary>The public URL Monday signs is short-lived, so it is asked for when the file is fetched.</summary>
    public async Task<Stream> FetchAttachmentAsync(SourceAttachment attachment, CancellationToken ct)
    {
        var data = await _client.QueryAsync(AssetQuery, new JsonObject { ["ids"] = new JsonArray(attachment.Id) }, ct);
        var url = (string?)data["assets"]?.AsArray().FirstOrDefault()?["public_url"];
        return Uri.TryCreate(url, UriKind.Absolute, out var absolute)
            ? await _client.DownloadAsync(absolute, ct)
            : throw new SourceAccessException($"Monday has no file {attachment.Id} ({attachment.FileName}) any more.", HttpStatusCode.NotFound);
    }

    private async IAsyncEnumerable<JsonObject> ItemsAsync(string board, [EnumeratorCancellation] CancellationToken ct)
    {
        var data = await _client.QueryAsync(FirstPageQuery, new JsonObject { ["board"] = new JsonArray(board), ["limit"] = PageSize }, ct);
        var page = data["boards"]?.AsArray().FirstOrDefault()?["items_page"];
        while (page is not null)
        {
            foreach (var item in page["items"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                yield return item;
            }

            if ((string?)page["cursor"] is not { Length: > 0 } next)
            {
                yield break;
            }

            data = await _client.QueryAsync(NextPageQuery, new JsonObject { ["cursor"] = next, ["limit"] = PageSize }, ct);
            page = data["next_items_page"];
        }
    }

    private static SourceItem Item(Catalog catalog, Board board, JsonObject item, string? parent, out DateTimeOffset? resolved)
    {
        var id = (string)item["id"]!;
        var values = (item["column_values"]?.AsArray().OfType<JsonObject>() ?? []).ToDictionary(v => (string)v["id"]!, StringComparer.Ordinal);
        string? Text(string? column) => column is not null && values.TryGetValue(column, out var v) && (string?)v["text"] is { Length: > 0 } t ? t : null;
        JsonNode? Value(string? column) => column is not null && values.TryGetValue(column, out var v) && (string?)v["value"] is { Length: > 0 } json ? JsonNode.Parse(json) : null;

        var status = Text(board.Status) ?? NoStatus;
        var created = Time(item["created_at"]) ?? catalog.ReadAt;
        var updated = Time(item["updated_at"]) ?? created;
        resolved = board.Status is not null && catalog.Done.Contains((board.Id, board.Status, status)) ? updated : null;
        var timeline = Value(board.Timeline);
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (parent is null && (string?)item["group"]?["id"] is { } groupId && board.Groups.FirstOrDefault(g => g.Id == groupId) is { } group)
        {
            fields[GroupField] = FieldValue.Text(group.Title);
        }

        foreach (var (column, kind) in board.Fields)
        {
            if (Text(column) is not { } text)
            {
                continue;
            }

            JsonElement? value = kind switch
            {
                FieldKind.Number => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? FieldValue.Number(number) : null,
                FieldKind.Date => DateOnly.TryParse(text[..Math.Min(10, text.Length)], CultureInfo.InvariantCulture, out var day) ? FieldValue.Date(day) : null,
                _ => FieldValue.Text(text),
            };
            if (value is { } v)
            {
                fields[catalog.FieldIds[(board.Id, column)]] = v;
            }
        }

        decimal? Number(string? column) => decimal.TryParse(Text(column), NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : null;
        return new SourceItem
        {
            Id = id,
            Project = board.Id,
            Type = parent is null ? ItemType : SubitemType,
            Status = status,
            Title = (string?)item["name"] ?? id,
            Parent = parent ?? ((string?)item["group"]?["id"] is { } g ? GroupId(board, g) : null),
            Assignee = Value(board.People)?["personsAndTeams"]?.AsArray().FirstOrDefault(p => (string?)p?["kind"] == "person")?["id"]?.ToString(),
            Creator = item["creator_id"]?.ToString(),
            Labels = [.. board.Labels.Select(Text).OfType<string>().SelectMany(t => t.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))],
            Fields = fields,
            EstimateSeconds = Number(board.Estimate) is { } hours ? (long)(hours * 3600) : null,
            Points = Number(board.Points),
            Start = Day((string?)timeline?["from"]),
            Due = Day((string?)timeline?["to"]) ?? Day((string?)Value(board.Date)?["date"]),
            Url = $"{catalog.Web}/boards/{board.Id}/pulses/{id}",
            Created = created,
            Updated = updated,
            Resolved = resolved,
        };
    }

    /// <summary>The items page carries an item's first 100 updates; one with more has the rest paged in.</summary>
    private async Task AllUpdatesAsync(JsonObject item, CancellationToken ct)
    {
        if (item["updates"] is not JsonArray updates || updates.Count < PageSize)
        {
            return;
        }

        for (var page = 2; ; page++)
        {
            var data = await _client.QueryAsync(UpdatesQuery, new JsonObject { ["ids"] = new JsonArray((string)item["id"]!), ["page"] = page }, ct);
            var more = data["items"]?.AsArray().FirstOrDefault()?["updates"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
            foreach (var update in more)
            {
                updates.Add(update.DeepClone());
            }

            if (more.Count < PageSize)
            {
                return;
            }
        }
    }

    private static IEnumerable<SourceComment> Comments(string item, JsonObject source)
    {
        foreach (var update in source["updates"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            foreach (var post in (update["replies"]?.AsArray().OfType<JsonObject>() ?? []).Prepend(update))
            {
                if ((string?)post["body"] is not { Length: > 0 } body || Time(post["created_at"]) is not { } created)
                {
                    continue;
                }

                yield return new SourceComment
                {
                    Id = (string)post["id"]!,
                    Item = item,
                    Author = post["creator_id"]?.ToString(),
                    Body = body,
                    Markup = MarkupKind.Html,
                    Created = created,
                    Updated = Time(post["updated_at"]) is { } u && u > created ? u : null,
                };
            }
        }
    }

    private static IEnumerable<SourceAttachment> Files(string item, JsonObject source) =>
        from asset in source["assets"]?.AsArray().OfType<JsonObject>() ?? []
        let created = Time(asset["created_at"])
        where created is not null
        select new SourceAttachment
        {
            Id = (string)asset["id"]!,
            Item = item,
            FileName = (string?)asset["name"] ?? "file",
            Size = asset["file_size"] is JsonValue size && size.TryGetValue<long>(out var bytes) ? bytes : null,
            Author = asset["uploaded_by"]?["id"]?.ToString(),
            Created = created.Value,
        };

    /// <summary>
    /// The board's activity log as changes, for the items read: the status column (labels), the people column
    /// (assignee) and renames. Monday keeps the log for a limited time, so older history isn't there to read.
    /// </summary>
    private async IAsyncEnumerable<SourceChange> ChangesAsync(Board board, HashSet<string> items, DateTimeOffset? since, [EnumeratorCancellation] CancellationToken ct)
    {
        for (var page = 1; ; page++)
        {
            // A delta asks Monday for the log since the cursor only; the check below stays exact.
            var variables = new JsonObject { ["board"] = new JsonArray(board.Id), ["page"] = page };
            if (since is { } from)
            {
                variables["from"] = from.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            }

            var data = await _client.QueryAsync(since is null ? LogQuery : LogSinceQuery, variables, ct);
            var logs = data["boards"]?.AsArray().FirstOrDefault()?["activity_logs"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
            foreach (var log in logs)
            {
                if (LogTime((string?)log["created_at"]) is not { } at || (since is not null && at < since)
                    || (string?)log["data"] is not { } text || JsonNode.Parse(text) is not JsonObject change
                    || change["pulse_id"]?.ToString() is not { } item || !items.Contains(item))
                {
                    continue;
                }

                SourceChange Change(string field, string? from, string? to, string? fromText = null, string? toText = null) => new()
                {
                    Id = (string)log["id"]!,
                    Item = item,
                    Actor = log["user_id"]?.ToString(),
                    At = at,
                    Field = field,
                    From = from,
                    To = to,
                    FromText = fromText,
                    ToText = toText,
                };

                var column = (string?)change["column_id"];
                if ((string?)log["event"] == "update_name")
                {
                    yield return Change("title", (string?)change["previous_value"], (string?)change["value"]);
                }
                else if (column is not null && column == board.Status)
                {
                    yield return Change("status", (string?)change["previous_value"]?["label"]?["text"] ?? NoStatus, (string?)change["value"]?["label"]?["text"] ?? NoStatus);
                }
                else if (column is not null && column == board.People)
                {
                    static string? Person(JsonNode? value) =>
                        value?["personsAndTeams"]?.AsArray().FirstOrDefault(p => (string?)p?["kind"] == "person")?["id"]?.ToString();
                    yield return Change("assignee", Person(change["previous_value"]), Person(change["value"]));
                }
            }

            if (logs.Count < PageSize)
            {
                yield break;
            }
        }
    }

    private static string GroupId(Board board, string group) => $"{board.Id}:{group}";

    private static DateTimeOffset? Time(JsonNode? value) =>
        (string?)value is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    /// <summary>An activity log time: a 17-digit count of 100-nanosecond ticks since 1970.</summary>
    private static DateTimeOffset? LogTime(string? text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ? DateTimeOffset.UnixEpoch.AddTicks(ticks) : null;

    private static DateOnly? Day(string? text) =>
        text is { Length: >= 10 } && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    private async Task<Catalog> CatalogAsync(CancellationToken ct)
    {
        if (_catalog is not null)
        {
            return _catalog;
        }

        var account = await _client.QueryAsync(AccountQuery, null, ct);
        var web = (string?)account["me"]?["account"]?["slug"] is { Length: > 0 } slug ? $"https://{slug}.monday.com" : "https://monday.com";

        var raw = new List<JsonObject>();
        for (var page = 1; ; page++)
        {
            var data = await _client.QueryAsync(BoardsQuery, new JsonObject { ["page"] = page }, ct);
            var boards = data["boards"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
            raw.AddRange(boards);
            if (boards.Count < PageSize)
            {
                break;
            }
        }

        var all = raw.Where(b => (string?)b["state"] != "deleted").ToList();
        var statuses = new Dictionary<string, SourceStatus>(StringComparer.Ordinal) { [NoStatus] = new() { Id = NoStatus, Name = "No status" } };
        var done = new HashSet<(string, string, string)>();
        var described = new List<SourceColumn>();
        var fields = new Dictionary<string, SourceField>(StringComparer.Ordinal) { [GroupField] = new() { Id = GroupField, Name = "Group", Kind = FieldKind.Select } };
        var fieldIds = new Dictionary<(string, string), string>();

        // A board's subitems column names its sub-item board, so that board's labels count only when its board is read.
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parent in all.Where(x => (string?)x["type"] is null or "board"))
        {
            foreach (var column in parent["columns"]?.AsArray().OfType<JsonObject>().Where(x => (string?)x["type"] == "subtasks") ?? [])
            {
                if ((string?)column["settings_str"] is { } subtasks && JsonNode.Parse(subtasks) is JsonObject settings && settings["boardIds"] is JsonArray ids)
                {
                    foreach (var sub in ids.OfType<JsonNode>())
                    {
                        parents.TryAdd(sub.ToString(), (string)parent["id"]!);
                    }
                }
            }
        }

        Board Read(JsonObject b)
        {
            var id = (string)b["id"]!;
            var columns = b["columns"]?.AsArray().OfType<JsonObject>().Select(c => (Id: (string)c["id"]!, Title: (string?)c["title"] ?? "", Type: (string?)c["type"] ?? "", Settings: (string?)c["settings_str"])).ToList() ?? [];
            string? First(Func<(string Id, string Title, string Type, string? Settings), bool> where) => columns.FirstOrDefault(where).Id;
            static bool Named(string title, string[] hints) => hints.Any(h => title.Contains(h, StringComparison.OrdinalIgnoreCase));

            // A role's column: the one named for it, else the first that fits. With two that fit and neither named,
            // the owner is asked.
            var asked = new HashSet<string>(StringComparer.Ordinal);
            string? Guess(string role, string type, string[] hints)
            {
                if (First(c => c.Type == type && Named(c.Title, hints)) is { } named)
                {
                    return named;
                }

                if (columns.Count(c => c.Type == type) > 1)
                {
                    asked.Add(role);
                }

                return First(c => c.Type == type);
            }

            var estimate = First(c => c.Type == "numbers" && Named(c.Title, ["estimat", "hour", "time"]));
            var board = new Board(
                id,
                (string?)b["name"] ?? id,
                (string?)b["state"] ?? "active",
                [.. b["groups"]?.AsArray().OfType<JsonObject>().Select(g => new Group((string)g["id"]!, (string?)g["title"] ?? "")) ?? []],
                Status: Guess(ColumnRoles.Status, "status", ["status", "stage", "progress"]),
                People: Guess(ColumnRoles.People, "people", ["owner", "assign", "person", "people"]),
                Timeline: Guess(ColumnRoles.Timeline, "timeline", ["timeline"]),
                Date: Guess(ColumnRoles.Date, "date", ["due", "deadline"]),
                Estimate: estimate,
                Points: First(c => c.Type == "numbers" && c.Id != estimate && Named(c.Title, ["point", "effort", "size"])),
                Labels: [.. columns.Where(c => c.Type is "tags" or "dropdown").Select(c => c.Id)],
                Fields: [],
                Columns: columns.ToDictionary(c => c.Id, c => c.Type, StringComparer.Ordinal),
                Eligible: []);
            var labels = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var column in columns.Where(c => c.Type == "status"))
            {
                labels[column.Id] = [];
                if (column.Settings is not { } settings || JsonNode.Parse(settings) is not JsonObject s)
                {
                    continue;
                }

                var doneIndexes = s["done_colors"]?.AsArray().Select(i => i?.ToString()).ToHashSet() ?? [];
                foreach (var (index, label) in s["labels"]?.AsObject() ?? [])
                {
                    if ((string?)label is { Length: > 0 } name)
                    {
                        // Statuses are labels by name across boards: one column calling a label done makes it done.
                        var isDone = doneIndexes.Contains(index);
                        if (!statuses.TryGetValue(name, out var known) || (isDone && known.Category is null))
                        {
                            statuses[name] = new SourceStatus { Id = name, Name = name, Category = isDone ? "done" : null };
                        }

                        labels[column.Id].Add(name);
                        if (isDone)
                        {
                            done.Add((id, column.Id, name));
                        }
                    }
                }
            }

            foreach (var column in columns.Where(c => !NotFields.Contains(c.Type) && !NotFields.Contains(c.Id) && !board.Labels.Contains(c.Id)))
            {
                var kind = column.Type switch
                {
                    "numbers" => FieldKind.Number,
                    "date" => FieldKind.Date,
                    "status" => FieldKind.Select,
                    _ => FieldKind.Text,
                };

                // Columns are per board: the same title and kind on two boards is one field. A column filling a role is
                // a field only once the owner picks another; the snapshot leaves out fields no column becomes.
                var fieldId = $"{column.Title}:{kind}".ToLowerInvariant();
                fields.TryAdd(fieldId, new SourceField { Id = fieldId, Name = column.Title, Kind = kind });
                fieldIds[(id, column.Id)] = fieldId;
                board.Eligible[column.Id] = kind;
            }

            var guessed = board.Roles();
            described.AddRange(columns.Where(c => board.Eligible.ContainsKey(c.Id) || Fits(c.Type).Length > 0).Select(c => new SourceColumn
            {
                Project = id,
                Id = c.Id,
                Name = c.Title,
                Roles = Fits(c.Type),
                Role = guessed.FirstOrDefault(r => r.Value == c.Id).Key,
                Ask = guessed.Any(r => r.Value == c.Id && asked.Contains(r.Key)),
                Labels = labels.GetValueOrDefault(c.Id) ?? [],
                Done = [.. done.Where(d => d.Item1 == id && d.Item2 == c.Id).Select(d => d.Item3)],
                Field = board.Eligible.ContainsKey(c.Id) ? fieldIds[(id, c.Id)] : null,
                Parent = parents.GetValueOrDefault(id),
            }));
            return board.Picked(null);
        }

        var boardsRead = all.Where(b => (string?)b["type"] is null or "board").Select(Read).ToList();
        var subboardIds = all.Where(b => (string?)b["type"] == "sub_items_board").Select(Read).ToDictionary(b => b.Id, StringComparer.Ordinal);

        var people = new List<SourcePerson>();
        for (var page = 1; ; page++)
        {
            var data = await _client.QueryAsync(UsersQuery, new JsonObject { ["page"] = page }, ct);
            var users = data["users"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
            people.AddRange(users.Select(u => new SourcePerson
            {
                Id = u["id"]!.ToString(),
                Name = (string?)u["name"] ?? u["id"]!.ToString(),
                Email = (string?)u["email"],
            }));
            if (users.Count < PageSize)
            {
                break;
            }
        }

        _catalog = new Catalog(web, _time.GetUtcNow(), boardsRead, subboardIds, done, fieldIds, new SourceDescription
        {
            Source = new SnapshotSource { Kind = SourceKinds.Monday, BaseUrl = web },
            Projects = [.. boardsRead.Select(b => new SourceProject { Id = b.Id, Name = b.Name, Url = $"{web}/boards/{b.Id}", Archived = b.State != "active" })],
            People = people,
            ItemTypes =
            [
                new SourceItemType { Id = GroupType, Name = "Group", Level = 1 },
                new SourceItemType { Id = ItemType, Name = "Item", Level = 0 },
                new SourceItemType { Id = SubitemType, Name = "Subitem", Level = -1 },
            ],
            Statuses = [.. statuses.Values],
            Fields = [.. fields.Values],
            Columns = described,
        });
        return _catalog;
    }

    private sealed record Group(string Id, string Title);

    /// <summary>The roles a Monday column type can fill.</summary>
    private static string[] Fits(string type) => type switch
    {
        "status" => [ColumnRoles.Status],
        "people" => [ColumnRoles.People],
        "timeline" => [ColumnRoles.Timeline],
        "date" => [ColumnRoles.Date],
        "numbers" => [ColumnRoles.Estimate, ColumnRoles.Points],
        _ => [],
    };

    /// <param name="Columns">Every column's type, by ID.</param>
    /// <param name="Eligible">The columns that can be fields; <see cref="Fields"/> are those of them filling no role.</param>
    private sealed record Board(
        string Id, string Name, string State, List<Group> Groups, string? Status, string? People, string? Timeline, string? Date,
        string? Estimate, string? Points, List<string> Labels, Dictionary<string, FieldKind> Fields,
        Dictionary<string, string> Columns, Dictionary<string, FieldKind> Eligible)
    {
        public Dictionary<string, string> Roles() => new Dictionary<string, string?>
        {
            [ColumnRoles.Status] = Status,
            [ColumnRoles.People] = People,
            [ColumnRoles.Timeline] = Timeline,
            [ColumnRoles.Date] = Date,
            [ColumnRoles.Estimate] = Estimate,
            [ColumnRoles.Points] = Points,
        }.Where(r => r.Value is not null).ToDictionary(r => r.Key, r => r.Value!, StringComparer.Ordinal);

        /// <summary>The board with the owner's column picks ("" for none) over the guesses, and its fields to match.</summary>
        public Board Picked(IReadOnlyDictionary<string, string>? picks)
        {
            string? Pick(string role, string? guess)
            {
                if (picks is null || !picks.TryGetValue(role, out var column))
                {
                    return guess;
                }

                if (column.Length == 0)
                {
                    return null;
                }

                return Columns.TryGetValue(column, out var type) && Fits(type).Contains(role)
                    ? column
                    : throw new SourceAccessException(
                        $"The Monday board {Name} has no column {column} that can be its {role}. Pick its columns again.", HttpStatusCode.NotFound);
            }

            var board = this with
            {
                Status = Pick(ColumnRoles.Status, Status),
                People = Pick(ColumnRoles.People, People),
                Timeline = Pick(ColumnRoles.Timeline, Timeline),
                Date = Pick(ColumnRoles.Date, Date),
                Estimate = Pick(ColumnRoles.Estimate, Estimate),
                Points = Pick(ColumnRoles.Points, Points),
            };
            var roles = board.Roles().Values.ToHashSet(StringComparer.Ordinal);
            return board with { Fields = Eligible.Where(e => !roles.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal) };
        }
    }

    private sealed record Catalog(
        string Web, DateTimeOffset ReadAt, List<Board> Boards, Dictionary<string, Board> Subboards, HashSet<(string Board, string Column, string Label)> Done,
        Dictionary<(string Board, string Column), string> FieldIds, SourceDescription Description);
}
