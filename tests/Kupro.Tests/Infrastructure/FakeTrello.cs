using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Kupro.Tests.Infrastructure;

/// <summary>
/// The Trello REST API for connector tests (task 026): serves the endpoints the connector calls, in the response shapes
/// of the API 1 docs, from in-memory boards. Like the real service it rejects a bad token with 401 "invalid token" and a
/// bad key with 401 "invalid key", answers 429 with <c>Retry-After</c> when told to, returns at most 1,000 cards or
/// actions per request (newest first, paged with <c>before</c>), 50 labels or actions without a <c>limit</c>, nested
/// card data only when asked for, and serves uploaded files only with the OAuth header. Every request is logged.
/// </summary>
internal sealed class FakeTrello : HttpMessageHandler
{
    public const string Key = "fake-key";
    public const string Token = "fake-token";
    public const string Host = "api.trello.com";

    private int _sequence;

    public FakeMember Me { get; set; } = new(Id(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), 1), "Sofie Larsen", "sofie", "sofie@acme.example");

    public List<FakeBoard> Boards { get; } = [];

    /// <summary>File bytes by absolute URL: Trello uploads under <c>https://api.trello.com/1/cards/…/download/…</c>, others anywhere.</summary>
    public Dictionary<string, byte[]> Files { get; } = [];

    /// <summary>The next N API requests are answered 429 with <see cref="RetryAfter"/>.</summary>
    public int Throttle { get; set; }

    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>"GET path?query" for every API request that passed auth, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Every host other than the API that was called.</summary>
    public List<string> ForeignHosts { get; } = [];

    /// <summary>Requests to another host that carried the key or token, in a header or the query (must stay empty).</summary>
    public List<string> Leaks { get; } = [];

    public HttpClient Client() => new(this);

    /// <summary>A Trello ID: an ObjectId whose first four bytes are the Unix time it was made.</summary>
    public static string Id(DateTimeOffset at, int n) =>
        $"{at.ToUnixTimeSeconds():x8}{n:x16}";

    public string NextId(DateTimeOffset at) => Id(at, Interlocked.Increment(ref _sequence) + 1000);

    public static string Header(string key, string token) => $"OAuth oauth_consumer_key=\"{key}\", oauth_token=\"{token}\"";

    public FakeBoard AddBoard(string name, DateTimeOffset created, bool closed = false)
    {
        var board = new FakeBoard(this, NextId(created), name, closed);
        Boards.Add(board);
        return board;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        var header = request.Headers.Authorization?.ToString() ?? "";
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        if (!url.Host.Equals(Host, StringComparison.OrdinalIgnoreCase))
        {
            lock (ForeignHosts)
            {
                ForeignHosts.Add(url.Authority);
                if (header.Contains(Token, StringComparison.Ordinal) || header.Contains(Key, StringComparison.Ordinal)
                    || url.Query.Contains(Token, StringComparison.Ordinal) || url.Query.Contains(Key, StringComparison.Ordinal))
                {
                    Leaks.Add(url.AbsoluteUri);
                }
            }

            return Task.FromResult(Files.TryGetValue(url.AbsoluteUri, out var foreign)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(foreign) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        // The header, or key and token in the query; both are accepted, like the real API.
        var (key, token) = header.Length > 0
            ? (header.Contains($"oauth_consumer_key=\"{Key}\"", StringComparison.Ordinal) ? Key : "?",
               header.Contains($"oauth_token=\"{Token}\"", StringComparison.Ordinal) ? Token : "?")
            : (query["key"], query["token"]);
        if (key != Key)
        {
            return Task.FromResult(Text(HttpStatusCode.Unauthorized, "invalid key"));
        }

        if (token != Token)
        {
            return Task.FromResult(Text(HttpStatusCode.Unauthorized, "invalid token"));
        }

        lock (Requests)
        {
            Requests.Add($"{request.Method} {url.PathAndQuery}");
            if (Throttle > 0)
            {
                Throttle--;
                var throttled = Text(HttpStatusCode.TooManyRequests, "API_TOKEN_LIMIT_EXCEEDED");
                throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(RetryAfter);
                return Task.FromResult(throttled);
            }
        }

        var segments = url.AbsolutePath.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        var response = Route(segments, query, header.Length > 0) ?? Text(HttpStatusCode.NotFound, "The requested resource was not found.");
        response.Headers.Add("x-rate-limit-api-token-interval-ms", "10000");
        response.Headers.Add("x-rate-limit-api-token-max", "100");
        response.Headers.Add("x-rate-limit-api-token-remaining", "99");
        return Task.FromResult(response);
    }

    private HttpResponseMessage? Route(string[] s, System.Collections.Specialized.NameValueCollection query, bool viaHeader)
    {
        FakeBoard? Board(string id) => Boards.FirstOrDefault(b => b.Id == id);
        var invalidLimit = query["limit"] is { } limitText && (!int.TryParse(limitText, CultureInfo.InvariantCulture, out var asked) || asked is < 1 or > 1000);
        if (invalidLimit)
        {
            return Text(HttpStatusCode.BadRequest, "invalid value for limit");
        }

        int Limit(int fallback) => query["limit"] is { } text ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;

        switch (s)
        {
            case ["1", "members", "me"]:
                return Json(Me.ToJson(withEmail: true));
            case ["1", "members", "me", "boards"]:
                return Array(Boards.Where(b => query["filter"] == "all" || !b.Closed).Select(b => b.ToJson()));
            case ["1", "boards", var id] when Board(id) is { } board:
                return Json(board.ToJson());
            case ["1", "boards", var id, ..] when Board(id) is null:
                return Text(HttpStatusCode.BadRequest, "invalid id");
            case ["1", "boards", var id, "lists"]:
                return Array(Board(id)!.Lists.Where(l => query["filter"] == "all" || l["closed"]!.GetValue<bool>() is false).Select(Clone));
            case ["1", "boards", var id, "labels"]:
                return Array(Board(id)!.Labels.Take(Limit(50)).Select(Clone));
            case ["1", "boards", var id, "members"]:
                return Array(Board(id)!.Members.Select(m => m.ToJson(withEmail: false)));
            case ["1", "boards", var id, "customFields"]:
                return Array(Board(id)!.CustomFields.Select(Clone));
            case ["1", "boards", var id, "cards", ..] when s.Length <= 5:
                var filter = s.Length == 5 ? s[4] : query["filter"] ?? "open";
                var cards = Board(id)!.Cards
                    .Where(c => filter == "all" || (filter == "closed") == c.Closed)
                    .Where(c => query["before"] is not { } before || string.CompareOrdinal(c.Id, before) < 0)
                    .OrderByDescending(c => c.Id, StringComparer.Ordinal)
                    .Take(Limit(1000));
                return Array(cards.Select(c => c.ToJson(query)));
            case ["1", "boards", var id, "actions"]:
                return Array(Actions(Board(id)!, query).Take(Limit(50)).Select(Clone));
            case ["1", "cards", var card, "attachments", var attachment, "download", var name]:
                // Trello serves uploads only to the OAuth header, not to key and token in the query.
                return viaHeader && Files.TryGetValue($"https://{Host}/1/cards/{card}/attachments/{attachment}/download/{Uri.EscapeDataString(name)}", out var bytes)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                    : Text(HttpStatusCode.Unauthorized, "unauthorized permission requested");
            default:
                return null;
        }
    }

    /// <summary>Newest first; <c>filter</c> takes types and <c>type:field</c> (an updateCard that changed that field).</summary>
    private static IEnumerable<JsonObject> Actions(FakeBoard board, System.Collections.Specialized.NameValueCollection query)
    {
        var filters = (query["filter"] ?? "all").Split(',');
        bool Matches(JsonObject action)
        {
            var type = action["type"]!.GetValue<string>();
            return filters.Any(f => f == "all" || f == type
                || (f.Split(':') is [var t, var field] && t == type && action["data"]?["old"]?.AsObject().ContainsKey(field) == true));
        }

        bool Before(JsonObject action, string bound, bool strictlyBefore)
        {
            var id = action["id"]!.GetValue<string>();
            var cmp = bound.Length == 24 && bound.All(char.IsAsciiHexDigitLower)
                ? string.CompareOrdinal(id, bound)
                : DateTimeOffset.Parse(action["date"]!.GetValue<string>(), CultureInfo.InvariantCulture)
                    .CompareTo(DateTimeOffset.Parse(bound, CultureInfo.InvariantCulture));
            return strictlyBefore ? cmp < 0 : cmp > 0;
        }

        return board.Actions
            .Where(Matches)
            .Where(a => query["before"] is not { } before || Before(a, before, strictlyBefore: true))
            .Where(a => query["since"] is not { } since || Before(a, since, strictlyBefore: false))
            .OrderByDescending(a => a["id"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    private static JsonObject Clone(JsonObject json) => json.DeepClone().AsObject();

    private static HttpResponseMessage Array(IEnumerable<JsonObject> values) =>
        Json(new JsonArray([.. values.Select(v => (JsonNode)v)]));

    private static HttpResponseMessage Json(JsonNode body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
}

internal sealed record FakeMember(string Id, string FullName, string Username, string? Email = null)
{
    public JsonObject ToJson(bool withEmail)
    {
        var json = new JsonObject { ["id"] = Id, ["fullName"] = FullName, ["username"] = Username };
        if (withEmail)
        {
            json["email"] = Email;
        }

        return json;
    }

    /// <summary>The short form in action data (<c>data.member</c>).</summary>
    public JsonObject Short() => new() { ["id"] = Id, ["name"] = FullName };
}

/// <summary>A board: lists, labels, members, custom fields, cards and the board's action log.</summary>
internal sealed class FakeBoard(FakeTrello trello, string id, string name, bool closed)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public bool Closed { get; } = closed;
    public FakeTrello Trello { get; } = trello;
    public List<JsonObject> Lists { get; } = [];
    public List<JsonObject> Labels { get; } = [];
    public List<FakeMember> Members { get; } = [];
    public List<JsonObject> CustomFields { get; } = [];
    public List<FakeCard> Cards { get; } = [];
    public List<JsonObject> Actions { get; } = [];

    public JsonObject ToJson() => new()
    {
        ["id"] = Id,
        ["name"] = Name,
        ["closed"] = Closed,
        ["url"] = $"https://trello.com/b/{Id[^8..]}/{Name.ToLowerInvariant()}",
    };

    public string AddList(string listName, bool archived = false)
    {
        var listId = Trello.NextId(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Lists.Add(new JsonObject { ["id"] = listId, ["name"] = listName, ["closed"] = archived, ["idBoard"] = Id, ["pos"] = 16384 * (Lists.Count + 1) });
        return listId;
    }

    public string AddLabel(string? labelName, string? color)
    {
        var labelId = Trello.NextId(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Labels.Add(new JsonObject { ["id"] = labelId, ["idBoard"] = Id, ["name"] = labelName ?? "", ["color"] = color });
        return labelId;
    }

    /// <summary>A custom field; for a list field, <paramref name="options"/> are its option texts (IDs come back in order).</summary>
    public (string Id, string[] Options) AddCustomField(string fieldName, string type, params string[] options)
    {
        var fieldId = Trello.NextId(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var optionIds = options.Select(_ => Trello.NextId(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero))).ToArray();
        var json = new JsonObject
        {
            ["id"] = fieldId,
            ["idModel"] = Id,
            ["modelType"] = "board",
            ["name"] = fieldName,
            ["type"] = type,
            ["pos"] = 16384 * (CustomFields.Count + 1),
            ["display"] = new JsonObject { ["cardFront"] = true },
        };
        if (options.Length > 0)
        {
            json["options"] = new JsonArray([.. options.Select((text, i) => (JsonNode)new JsonObject
            {
                ["id"] = optionIds[i],
                ["idCustomField"] = fieldId,
                ["value"] = new JsonObject { ["text"] = text },
                ["color"] = "none",
                ["pos"] = 16384 * (i + 1),
            })]);
        }

        CustomFields.Add(json);
        return (fieldId, optionIds);
    }

    /// <summary>A card created by <paramref name="by"/> at <paramref name="at"/>, with its <c>createCard</c> action.</summary>
    public FakeCard AddCard(string list, string title, DateTimeOffset at, FakeMember by)
    {
        var card = new FakeCard(this, Trello.NextId(at), Cards.Count + 1, at) { IdList = list, Name = title };
        Cards.Add(card);
        card.Act(at, by, "createCard", new JsonObject { ["list"] = new JsonObject { ["id"] = list } });
        return card;
    }

    public JsonObject Act(DateTimeOffset at, FakeMember by, string type, JsonObject data)
    {
        data["board"] = new JsonObject { ["id"] = Id, ["name"] = Name };
        var action = new JsonObject
        {
            ["id"] = Trello.NextId(at),
            ["idMemberCreator"] = by.Id,
            ["type"] = type,
            ["date"] = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["data"] = data,
            ["memberCreator"] = by.ToJson(withEmail: false),
        };
        Actions.Add(action);
        return action;
    }
}

/// <summary>
/// A card. The verbs (<see cref="Comment"/>, <see cref="Move"/>, <see cref="Update"/>, …) log the board action in the
/// shape Trello writes it and apply it to the card, so the card and its history agree.
/// </summary>
internal sealed class FakeCard(FakeBoard board, string id, int idShort, DateTimeOffset created)
{
    public string Id { get; } = id;
    public int IdShort { get; } = idShort;
    public required string IdList { get; set; }
    public required string Name { get; set; }
    public string Desc { get; set; } = "";
    public bool Closed { get; set; }
    public bool DueComplete { get; set; }
    public string? Due { get; set; }
    public string? Start { get; set; }
    public DateTimeOffset LastActivity { get; private set; } = created;
    public List<string> IdLabels { get; } = [];
    public List<FakeMember> Members { get; } = [];
    public List<JsonObject> Attachments { get; } = [];
    public List<JsonObject> Checklists { get; } = [];
    public List<JsonObject> CustomFieldItems { get; } = [];

    public JsonObject Act(DateTimeOffset at, FakeMember by, string type, JsonObject data, JsonObject? card = null)
    {
        card ??= [];
        card["id"] = Id;
        if (!card.ContainsKey("name"))
        {
            // A rename carries the new name here already.
            card["name"] = Name;
        }

        card["idShort"] = IdShort;
        card["shortLink"] = Id[^8..];
        data["card"] = card;
        LastActivity = at > LastActivity ? at : LastActivity;
        return board.Act(at, by, type, data);
    }

    public FakeCard Comment(DateTimeOffset at, FakeMember by, string text, DateTimeOffset? edited = null)
    {
        var data = new JsonObject { ["text"] = text };
        if (edited is { } e)
        {
            data["dateLastEdited"] = e.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        Act(at, by, "commentCard", data);
        return this;
    }

    public FakeCard Move(DateTimeOffset at, FakeMember by, string toList)
    {
        string NameOf(string list) => board.Lists.Single(l => l["id"]!.GetValue<string>() == list)["name"]!.GetValue<string>();
        Act(at, by, "updateCard", new JsonObject
        {
            ["old"] = new JsonObject { ["idList"] = IdList },
            ["listBefore"] = new JsonObject { ["id"] = IdList, ["name"] = NameOf(IdList) },
            ["listAfter"] = new JsonObject { ["id"] = toList, ["name"] = NameOf(toList) },
        }, new JsonObject { ["idList"] = toList });
        IdList = toList;
        return this;
    }

    /// <summary>An <c>updateCard</c> of one field: the old value in <c>data.old</c>, the new one on <c>data.card</c>.</summary>
    public FakeCard Update(DateTimeOffset at, FakeMember by, string field, JsonNode? old, JsonNode? @new)
    {
        Act(at, by, "updateCard", new JsonObject { ["old"] = new JsonObject { [field] = old?.DeepClone() } }, new JsonObject { [field] = @new?.DeepClone() });
        switch (field)
        {
            case "name": Name = @new!.GetValue<string>(); break;
            case "desc": Desc = @new!.GetValue<string>(); break;
            case "due": Due = @new?.GetValue<string>(); break;
            case "start": Start = @new?.GetValue<string>(); break;
            case "closed": Closed = @new!.GetValue<bool>(); break;
            case "dueComplete": DueComplete = @new!.GetValue<bool>(); break;
        }

        return this;
    }

    public FakeCard Archive(DateTimeOffset at, FakeMember by) => Update(at, by, "closed", false, true);

    public FakeCard AddMember(DateTimeOffset at, FakeMember by, FakeMember member)
    {
        Act(at, by, "addMemberToCard", new JsonObject { ["idMember"] = member.Id, ["member"] = member.Short() });
        Members.Add(member);
        return this;
    }

    public FakeCard AddLabel(DateTimeOffset at, FakeMember by, string label)
    {
        var json = board.Labels.Single(l => l["id"]!.GetValue<string>() == label);
        Act(at, by, "addLabelToCard", new JsonObject { ["label"] = json.DeepClone(), ["value"] = json["name"]?.DeepClone() });
        IdLabels.Add(label);
        return this;
    }

    /// <summary>Sets a custom field. <paramref name="value"/> is the value object (<c>{"number": "5"}</c>) or a list option ID.</summary>
    public FakeCard SetField(DateTimeOffset at, FakeMember by, string field, JsonNode value)
    {
        var item = new JsonObject { ["id"] = board.Trello.NextId(at), ["idCustomField"] = field, ["idModel"] = Id, ["modelType"] = "card" };
        if (value is JsonObject valueObject)
        {
            item["value"] = valueObject.DeepClone();
        }
        else
        {
            item["idValue"] = value.DeepClone();
        }

        var old = CustomFieldItems.FirstOrDefault(i => i["idCustomField"]!.GetValue<string>() == field);
        var fieldJson = board.CustomFields.Single(f => f["id"]!.GetValue<string>() == field);
        Act(at, by, "updateCustomFieldItem", new JsonObject
        {
            ["customField"] = new JsonObject { ["id"] = field, ["name"] = fieldJson["name"]!.DeepClone(), ["type"] = fieldJson["type"]!.DeepClone() },
            ["customFieldItem"] = item.DeepClone(),
            ["old"] = old is null ? new JsonObject { ["value"] = null } : new JsonObject { ["value"] = old["value"]?.DeepClone(), ["idValue"] = old["idValue"]?.DeepClone() },
        });
        if (old is not null)
        {
            CustomFieldItems.Remove(old);
        }

        CustomFieldItems.Add(item);
        return this;
    }

    public string AddChecklist(string checklistName, params (string Name, bool Complete, FakeMember? Member, string? Due)[] items)
    {
        var at = new DateTimeOffset(2025, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var checklistId = board.Trello.NextId(at);
        Checklists.Add(new JsonObject
        {
            ["id"] = checklistId,
            ["name"] = checklistName,
            ["idBoard"] = board.Id,
            ["idCard"] = Id,
            ["pos"] = 16384 * (Checklists.Count + 1),
            // Listed in reverse on purpose: Trello's order is "pos", not the array's.
            ["checkItems"] = new JsonArray([.. items.Select((item, i) => (JsonNode)new JsonObject
            {
                ["id"] = board.Trello.NextId(at),
                ["idChecklist"] = checklistId,
                ["name"] = item.Name,
                ["state"] = item.Complete ? "complete" : "incomplete",
                ["pos"] = 16384 * (i + 1),
                ["due"] = item.Due,
                ["idMember"] = item.Member?.Id,
            }).Reverse()]),
        });
        return checklistId;
    }

    public FakeCard Tick(DateTimeOffset at, FakeMember by, string checkItemName, bool complete)
    {
        var checklist = Checklists.Single(c => c["checkItems"]!.AsArray().Any(i => i!["name"]!.GetValue<string>() == checkItemName));
        var item = checklist["checkItems"]!.AsArray().Single(i => i!["name"]!.GetValue<string>() == checkItemName)!.AsObject();
        item["state"] = complete ? "complete" : "incomplete";
        Act(at, by, "updateCheckItemStateOnCard", new JsonObject
        {
            ["checkItem"] = new JsonObject { ["id"] = item["id"]!.DeepClone(), ["name"] = checkItemName, ["state"] = item["state"]!.DeepClone() },
            ["checklist"] = new JsonObject { ["id"] = checklist["id"]!.DeepClone(), ["name"] = checklist["name"]!.DeepClone() },
        });
        return this;
    }

    /// <summary>An uploaded file (served at its <c>api.trello.com</c> download URL) or a link attachment.</summary>
    public string Attach(DateTimeOffset at, FakeMember by, string attachmentName, string? url = null, byte[]? bytes = null, string? mimeType = null)
    {
        var attachmentId = board.Trello.NextId(at);
        var upload = url is null;
        url ??= $"https://trello.com/1/cards/{Id}/attachments/{attachmentId}/download/{Uri.EscapeDataString(attachmentName)}";
        if (upload && bytes is not null)
        {
            board.Trello.Files[$"https://{FakeTrello.Host}/1/cards/{Id}/attachments/{attachmentId}/download/{Uri.EscapeDataString(attachmentName)}"] = bytes;
        }

        Attachments.Add(new JsonObject
        {
            ["id"] = attachmentId,
            ["bytes"] = upload ? bytes?.Length : null,
            ["date"] = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["idMember"] = by.Id,
            ["isUpload"] = upload,
            ["mimeType"] = upload ? mimeType : "",
            ["name"] = attachmentName,
            ["url"] = url,
            ["fileName"] = upload ? attachmentName : null,
            ["pos"] = 16384 * (Attachments.Count + 1),
        });
        Act(at, by, "addAttachmentToCard", new JsonObject { ["attachment"] = new JsonObject { ["id"] = attachmentId, ["name"] = attachmentName, ["url"] = url } });
        return attachmentId;
    }

    /// <summary>The card as <c>boards/{id}/cards</c> returns it; nested data only when the query asks for it.</summary>
    public JsonObject ToJson(System.Collections.Specialized.NameValueCollection query)
    {
        var json = new JsonObject
        {
            ["id"] = Id,
            ["idShort"] = IdShort,
            ["idBoard"] = board.Id,
            ["idList"] = IdList,
            ["name"] = Name,
            ["desc"] = Desc,
            ["closed"] = Closed,
            ["due"] = Due,
            ["start"] = Start,
            ["dueComplete"] = DueComplete,
            ["dateLastActivity"] = LastActivity.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["idLabels"] = new JsonArray([.. IdLabels.Select(l => (JsonNode)l)]),
            ["idMembers"] = new JsonArray([.. Members.Select(m => (JsonNode)m.Id)]),
            ["shortUrl"] = $"https://trello.com/c/{Id[^8..]}",
            ["url"] = $"https://trello.com/c/{Id[^8..]}/{IdShort}-{Name.ToLowerInvariant().Replace(' ', '-')}",
            ["pos"] = 16384 * IdShort,
        };
        if (query["members"] == "true")
        {
            json["members"] = new JsonArray([.. Members.Select(m => (JsonNode)m.ToJson(withEmail: false))]);
        }

        if (query["attachments"] == "true")
        {
            json["attachments"] = new JsonArray([.. Attachments.Select(a => (JsonNode)a.DeepClone())]);
        }

        if (query["checklists"] == "all")
        {
            json["checklists"] = new JsonArray([.. Checklists.Select(c => (JsonNode)c.DeepClone())]);
        }

        if (query["customFieldItems"] == "true")
        {
            json["customFieldItems"] = new JsonArray([.. CustomFieldItems.Select(c => (JsonNode)c.DeepClone())]);
        }

        return json;
    }
}
