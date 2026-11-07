using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Kupro.Tests.Infrastructure;

/// <summary>
/// Linear's GraphQL API for connector tests (task 026). A small GraphQL executor over in-memory JSON: it parses the
/// query, resolves the root fields the connector uses with Linear's filters (<c>eq</c>, <c>in</c>, <c>gte</c>,
/// <c>some</c>, nested objects), pages connections Relay-style (<c>first</c> ≤ 250, <c>after</c>, <c>pageInfo</c>),
/// returns only the fields asked for, and prices the query the way Linear documents (object 1, scalar 0.1, connection
/// times <c>first</c> or 50), rejecting one over 10,000. Like the real service it answers a bad credential with an
/// <c>AUTHENTICATION_ERROR</c>, a throttled request with <c>RATELIMITED</c>, sends <c>X-RateLimit-*</c> headers (resets in
/// epoch milliseconds), and serves <c>uploads.linear.app</c> files only with the credential. Every request is logged.
/// </summary>
internal sealed class FakeLinear : HttpMessageHandler
{
    public const string ApiKey = "lin_api_fake0123456789";
    public const string OAuthToken = "lin_oauth_fake0123456789";
    public const string Host = "api.linear.app";
    public const string UploadHost = "uploads.linear.app";
    public const int MaxFirst = 250;
    public const int MaxComplexity = 10_000;
    public const int RequestLimit = 2_500;

    private int _sequence;
    private readonly Dictionary<string, int> _numbers = [];

    public JsonObject Organization { get; } = new() { ["id"] = "org-acme", ["name"] = "Acme", ["urlKey"] = "acme" };
    public List<JsonObject> Teams { get; } = [];
    public List<JsonObject> States { get; } = [];
    public List<JsonObject> Users { get; } = [];
    public List<JsonObject> Labels { get; } = [];
    public List<JsonObject> ProjectStatuses { get; } = [];
    public List<JsonObject> Cycles { get; } = [];
    public List<JsonObject> Projects { get; } = [];
    public List<FakeIssue> Issues { get; } = [];
    public List<JsonObject> Comments { get; } = [];

    /// <summary>File bytes by absolute URL: uploads under <c>https://uploads.linear.app/…</c>, others anywhere.</summary>
    public Dictionary<string, byte[]> Files { get; } = [];

    /// <summary>The time the fake judges rate-limit resets by.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>The next N API requests are answered <c>RATELIMITED</c> with <see cref="ThrottleStatus"/>.</summary>
    public int Throttle { get; set; }

    /// <summary>Linear documents 429; older answers were 400 with the same error code.</summary>
    public HttpStatusCode ThrottleStatus { get; set; } = HttpStatusCode.TooManyRequests;

    /// <summary><c>Retry-After</c> on a throttled answer; null leaves only the reset headers.</summary>
    public TimeSpan? RetryAfter { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Requests left in the hour; at zero the API answers <c>RATELIMITED</c> until <see cref="RequestsReset"/>.</summary>
    public int RequestsRemaining { get; set; } = RequestLimit;

    public DateTimeOffset RequestsReset { get; set; } = DateTimeOffset.UtcNow.AddHours(1);

    /// <summary>How many requests were answered <c>RATELIMITED</c>.</summary>
    public int Throttled { get; private set; }

    /// <summary>"Operation {variables}" for every API request that passed auth, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>The complexity of every query that ran.</summary>
    public List<double> Complexities { get; } = [];

    /// <summary>"GET path" for every upload served.</summary>
    public List<string> Downloads { get; } = [];

    /// <summary>Every host other than Linear's that was called.</summary>
    public List<string> ForeignHosts { get; } = [];

    /// <summary>Requests to another host that carried the key or token (must stay empty).</summary>
    public List<string> Leaks { get; } = [];

    public HttpClient Client() => new(this);

    public string NextId() => $"00000000-0000-4000-8000-{Interlocked.Increment(ref _sequence):x12}";

    public static JsonObject Ref(JsonObject entity) => new() { ["id"] = Id(entity) };

    public static string Id(JsonObject entity) => entity["id"]!.GetValue<string>();

    public static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public JsonObject AddUser(string name, string displayName, string? email, bool active = true)
    {
        var user = new JsonObject { ["id"] = NextId(), ["name"] = name, ["displayName"] = displayName, ["email"] = email, ["active"] = active };
        Users.Add(user);
        return user;
    }

    public JsonObject AddTeam(string key, string name, DateTimeOffset? archived = null)
    {
        var team = new JsonObject { ["id"] = NextId(), ["key"] = key, ["name"] = name, ["archivedAt"] = archived is { } a ? Time(a) : null };
        Teams.Add(team);
        return team;
    }

    public JsonObject AddState(JsonObject team, string name, string type)
    {
        var position = States.Count(s => s["team"]!["id"]!.GetValue<string>() == Id(team));
        var state = new JsonObject { ["id"] = NextId(), ["name"] = name, ["type"] = type, ["position"] = position, ["team"] = Ref(team) };
        States.Add(state);
        return state;
    }

    public JsonObject AddLabel(string name, JsonObject? team = null, JsonObject? group = null, bool isGroup = false)
    {
        var label = new JsonObject
        {
            ["id"] = NextId(),
            ["name"] = name,
            ["isGroup"] = isGroup,
            ["parent"] = group is null ? null : Ref(group),
            ["team"] = team is null ? null : Ref(team),
        };
        Labels.Add(label);
        return label;
    }

    public JsonObject AddProjectStatus(string name, string type)
    {
        var status = new JsonObject { ["id"] = NextId(), ["name"] = name, ["type"] = type, ["position"] = ProjectStatuses.Count };
        ProjectStatuses.Add(status);
        return status;
    }

    public JsonObject AddCycle(JsonObject team, int number, DateTimeOffset starts, DateTimeOffset ends, DateTimeOffset? completed = null, string? name = null)
    {
        var cycle = new JsonObject
        {
            ["id"] = NextId(),
            ["number"] = number,
            ["name"] = name,
            ["startsAt"] = Time(starts),
            ["endsAt"] = Time(ends),
            ["completedAt"] = completed is { } c ? Time(c) : null,
            ["createdAt"] = Time(starts.AddDays(-1)),
            ["team"] = Ref(team),
        };
        Cycles.Add(cycle);
        return cycle;
    }

    /// <summary>A project in <paramref name="teams"/>, in that order. Set the other fields on the returned JSON.</summary>
    public JsonObject AddProject(string name, DateTimeOffset created, JsonObject status, JsonObject? creator, params JsonObject[] teams)
    {
        var id = NextId();
        var project = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["description"] = "",
            ["content"] = null,
            ["url"] = $"https://linear.app/acme/project/{name.ToLowerInvariant().Replace(' ', '-')}-{id[^6..]}",
            ["createdAt"] = Time(created),
            ["updatedAt"] = Time(created),
            ["startDate"] = null,
            ["targetDate"] = null,
            ["completedAt"] = null,
            ["canceledAt"] = null,
            ["archivedAt"] = null,
            ["priority"] = 0,
            ["trashed"] = null,
            ["status"] = Ref(status),
            ["lead"] = null,
            ["creator"] = creator is null ? null : Ref(creator),
            ["teams"] = new JsonArray([.. teams.Select(t => (JsonNode)Ref(t))]),
            ["accessibleTeams"] = new JsonArray([.. teams.Select(t => (JsonNode)Ref(t))]),
            ["labels"] = new JsonArray(),
            ["initiatives"] = new JsonArray(),
            ["projectMilestones"] = new JsonArray(),
        };
        Projects.Add(project);
        return project;
    }

    public JsonObject AddMilestone(JsonObject project, string name, string? targetDate, string status)
    {
        var milestone = new JsonObject { ["id"] = NextId(), ["name"] = name, ["targetDate"] = targetDate, ["status"] = status };
        project["projectMilestones"]!.AsArray().Add(milestone);
        return milestone;
    }

    public FakeIssue AddIssue(JsonObject team, string title, JsonObject state, DateTimeOffset created, JsonObject? creator)
    {
        var key = team["key"]!.GetValue<string>();
        var number = _numbers[key] = _numbers.GetValueOrDefault(key) + 1;
        var issue = new FakeIssue(this, NextId(), $"{key}-{number}", team, state, title, created, creator);
        Issues.Add(issue);
        return issue;
    }

    /// <summary>An uploaded file: its URL on the upload host, served with the credential only.</summary>
    public string Upload(byte[] bytes)
    {
        var url = $"https://{UploadHost}/{Organization["id"]}/{NextId()}/{NextId()}";
        Files[url] = bytes;
        return url;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        var header = request.Headers.TryGetValues("Authorization", out var values) ? string.Join(",", values) : "";
        var authorized = header is ApiKey || header == "Bearer " + OAuthToken;
        if (url.Host.Equals(UploadHost, StringComparison.OrdinalIgnoreCase))
        {
            lock (Downloads)
            {
                Downloads.Add($"{request.Method} {url.AbsolutePath}");
            }

            return !authorized ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : Files.TryGetValue(url.AbsoluteUri, out var upload) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(upload) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (!url.Host.Equals(Host, StringComparison.OrdinalIgnoreCase))
        {
            lock (ForeignHosts)
            {
                ForeignHosts.Add(url.Authority);
                if (header.Contains(ApiKey, StringComparison.Ordinal) || header.Contains(OAuthToken, StringComparison.Ordinal)
                    || url.Query.Contains(ApiKey, StringComparison.Ordinal) || url.Query.Contains(OAuthToken, StringComparison.Ordinal))
                {
                    Leaks.Add(url.AbsoluteUri);
                }
            }

            return Files.TryGetValue(url.AbsoluteUri, out var foreign)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(foreign) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (request.Method != HttpMethod.Post || url.AbsolutePath != "/graphql")
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (!authorized)
        {
            // Linear answers a missing or wrong key with a GraphQL error, not a bare 401.
            return Error(HttpStatusCode.BadRequest, "Authentication required, not authenticated", "AUTHENTICATION_ERROR",
                "You need to authenticate to access this operation.");
        }

        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        var variables = body["variables"] as JsonObject ?? [];
        lock (Requests)
        {
            Requests.Add($"{body["operationName"]} {variables.ToJsonString()}");
            var now = Clock();
            if (now >= RequestsReset)
            {
                RequestsRemaining = RequestLimit;
                RequestsReset = now.AddHours(1);
            }

            if (Throttle > 0 || RequestsRemaining <= 0)
            {
                Throttle = Math.Max(0, Throttle - 1);
                Throttled++;
                var throttled = Error(ThrottleStatus, "Rate limit exceeded", "RATELIMITED", "Too many requests. Try again later.");
                if (RetryAfter is { } retry)
                {
                    throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retry);
                }

                RateHeaders(throttled, 0);
                return throttled;
            }

            RequestsRemaining--;
        }

        List<GqlField> selection;
        try
        {
            selection = new GqlParser(body["query"]!.GetValue<string>(), variables).Document();
        }
        catch (FormatException e)
        {
            return Error(HttpStatusCode.BadRequest, e.Message, "GRAPHQL_PARSE_FAILED", null);
        }

        var complexity = Math.Ceiling(selection.Sum(Cost));
        lock (Complexities)
        {
            Complexities.Add(complexity);
        }

        if (complexity > MaxComplexity)
        {
            return Error(HttpStatusCode.BadRequest, $"Query too complex: {complexity} exceeds {MaxComplexity}.", "INVALID_INPUT", "Query too complex");
        }

        HttpResponseMessage response;
        try
        {
            var data = new JsonObject();
            foreach (var field in selection)
            {
                data[field.Key] = Select(Root(field), field);
            }

            response = Json(HttpStatusCode.OK, new JsonObject { ["data"] = data });
        }
        catch (GqlException e)
        {
            response = Error(HttpStatusCode.BadRequest, e.Message, e.Code, e.Message);
        }

        RateHeaders(response, complexity);
        return response;
    }

    private void RateHeaders(HttpResponseMessage response, double complexity)
    {
        response.Headers.Add("X-RateLimit-Requests-Limit", RequestLimit.ToString(CultureInfo.InvariantCulture));
        response.Headers.Add("X-RateLimit-Requests-Remaining", Math.Max(0, RequestsRemaining).ToString(CultureInfo.InvariantCulture));
        response.Headers.Add("X-RateLimit-Requests-Reset", RequestsReset.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        response.Headers.Add("X-Complexity", complexity.ToString(CultureInfo.InvariantCulture));
        response.Headers.Add("X-RateLimit-Complexity-Limit", "3000000");
        response.Headers.Add("X-RateLimit-Complexity-Remaining", "2900000");
        response.Headers.Add("X-RateLimit-Complexity-Reset", RequestsReset.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>A root field's value before selection: an object, or an array a connection pages.</summary>
    private JsonNode? Root(GqlField field)
    {
        var args = field.Args;
        var archived = args["includeArchived"]?.GetValue<bool>() is true;
        IEnumerable<JsonObject> Filtered(IEnumerable<JsonObject> nodes) => nodes
            .Where(n => archived || n["archivedAt"] is null)
            .Where(n => args["filter"] is not JsonObject filter || Matches(n, filter));
        // Copies: a stored node can't sit in two arrays.
        JsonArray Array(IEnumerable<JsonObject> nodes) => new([.. nodes.Select(n => n.DeepClone())]);

        return field.Name switch
        {
            "organization" => Organization,
            "teams" => Array(Filtered(Teams)),
            "workflowStates" => Array(Filtered(States)),
            "users" => Array(Users.Where(u => args["includeDisabled"]?.GetValue<bool>() is true || u["active"]!.GetValue<bool>())),
            "issueLabels" => Array(Filtered(Labels)),
            "projectStatuses" => Array(ProjectStatuses),
            "cycles" => Array(Filtered(Cycles)),
            "projects" => Array(Filtered(Projects)),
            "issues" => Array(Filtered(Issues.Select(i => i.Json))),
            "comments" => Array(Filtered(Comments)),
            "issue" => Issues.FirstOrDefault(i => i.Id == args["id"]?.GetValue<string>())?.Json
                ?? throw new GqlException("Entity not found: Issue", "INVALID_INPUT"),
            _ => throw new GqlException($"Cannot query field \"{field.Name}\" on type \"Query\".", "GRAPHQL_VALIDATION_FAILED"),
        };
    }

    /// <summary>Linear's filter semantics: comparators on a value, <c>some</c>/<c>every</c> on a list, nesting on objects.</summary>
    private static bool Matches(JsonNode? value, JsonObject filter) => filter.All(pair =>
    {
        var (key, condition) = pair;
        return key switch
        {
            "and" => condition!.AsArray().All(f => Matches(value, f!.AsObject())),
            "or" => condition!.AsArray().Any(f => Matches(value, f!.AsObject())),
            "eq" => Compare(value, condition) == 0,
            "neq" => Compare(value, condition) != 0,
            "in" => condition!.AsArray().Any(c => Compare(value, c) == 0),
            "nin" => condition!.AsArray().All(c => Compare(value, c) != 0),
            "gt" => value is not null && Compare(value, condition) > 0,
            "gte" => value is not null && Compare(value, condition) >= 0,
            "lt" => value is not null && Compare(value, condition) < 0,
            "lte" => value is not null && Compare(value, condition) <= 0,
            "null" => (value is null) == condition!.GetValue<bool>(),
            "some" => value is JsonArray list && list.Any(e => Matches(e, condition!.AsObject())),
            "every" => value is JsonArray all && all.All(e => Matches(e, condition!.AsObject())),
            _ => condition is JsonObject nested && Matches(value?[key], nested),
        };
    });

    private static int Compare(JsonNode? a, JsonNode? b)
    {
        string? Text(JsonNode? n) => n is JsonValue v ? v.ToString() : n?.ToJsonString();
        var (x, y) = (Text(a), Text(b));
        if (x is null || y is null)
        {
            return x is null && y is null ? 0 : x is null ? -1 : 1;
        }

        return DateTimeOffset.TryParse(x, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dx)
            && DateTimeOffset.TryParse(y, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dy)
            ? dx.CompareTo(dy)
            : string.CompareOrdinal(x, y);
    }

    /// <summary>Projects <paramref name="value"/> onto the field's selection; an array under a selection is a connection.</summary>
    private static JsonNode? Select(JsonNode? value, GqlField field)
    {
        if (field.Selection is null)
        {
            return value?.DeepClone();
        }

        if (value is JsonArray array)
        {
            return Connection(array, field);
        }

        if (value is not JsonObject obj)
        {
            return null;
        }

        var result = new JsonObject();
        foreach (var child in field.Selection)
        {
            result[child.Key] = Select(obj[child.Name], child);
        }

        return result;
    }

    /// <summary>One page, oldest first (Linear's default order is <c>createdAt</c>); the cursor is the last node's ID.</summary>
    private static JsonObject Connection(JsonArray array, GqlField field)
    {
        var first = field.Args["first"]?.GetValue<int>() ?? 50;
        if (first is < 1 or > MaxFirst)
        {
            throw new GqlException($"Argument \"first\" must be between 1 and {MaxFirst}.", "INVALID_INPUT");
        }

        var nodes = array.OfType<JsonObject>()
            .OrderBy(n => n["createdAt"]?.GetValue<string>() is { } at ? DateTimeOffset.Parse(at, CultureInfo.InvariantCulture) : DateTimeOffset.MinValue)
            .ThenBy(n => n["id"]?.GetValue<string>(), StringComparer.Ordinal)
            .ToList();
        if (field.Args["after"]?.GetValue<string>() is { } after)
        {
            var index = nodes.FindIndex(n => n["id"]?.GetValue<string>() == after);
            nodes = index < 0 ? throw new GqlException("Invalid cursor.", "INVALID_INPUT") : nodes[(index + 1)..];
        }

        var page = nodes.Take(first).ToList();
        var result = new JsonObject();
        foreach (var child in field.Selection!)
        {
            result[child.Key] = child.Name switch
            {
                "nodes" => new JsonArray([.. page.Select(n => Select(n, child))]),
                "pageInfo" => Select(new JsonObject
                {
                    ["hasNextPage"] = nodes.Count > first,
                    ["endCursor"] = page.Count == 0 ? null : page[^1]["id"]!.DeepClone(),
                    ["hasPreviousPage"] = false,
                    ["startCursor"] = page.Count == 0 ? null : page[0]["id"]!.DeepClone(),
                }, child),
                _ => throw new GqlException($"Cannot query field \"{child.Name}\" on a connection.", "GRAPHQL_VALIDATION_FAILED"),
            };
        }

        return result;
    }

    /// <summary>Linear's documented pricing: a scalar 0.1, an object 1, a connection's nodes times <c>first</c> (50 if absent).</summary>
    private static double Cost(GqlField field)
    {
        if (field.Selection is null)
        {
            return 0.1;
        }

        if (field.Selection.Any(c => c.Name == "nodes"))
        {
            var first = field.Args["first"]?.GetValue<int>() ?? 50;
            return field.Selection.Sum(c => c.Name == "nodes" ? first * (1 + c.Selection!.Sum(Cost)) : Cost(c));
        }

        return 1 + field.Selection.Sum(Cost);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string message, string code, string? presentable) =>
        Json(status, new JsonObject
        {
            ["errors"] = new JsonArray(new JsonObject
            {
                ["message"] = message,
                ["extensions"] = new JsonObject
                {
                    ["code"] = code,
                    ["type"] = code.ToLowerInvariant().Replace('_', ' '),
                    ["userPresentableMessage"] = presentable,
                },
            }),
        });

    private sealed class GqlException(string message, string code) : Exception(message)
    {
        public string Code { get; } = code;
    }

    internal sealed record GqlField(string Key, string Name, JsonObject Args, List<GqlField>? Selection);

    /// <summary>Enough of GraphQL for the connector's queries: one operation, variables, arguments, nested selections.</summary>
    private sealed class GqlParser(string text, JsonObject variables)
    {
        private int _at;

        public List<GqlField> Document()
        {
            Skip();
            if (Peek() != '{')
            {
                var keyword = Name();
                if (keyword != "query")
                {
                    throw new FormatException($"Only queries are supported, not {keyword}.");
                }

                Skip();
                if (char.IsLetter(Peek()))
                {
                    Name();
                }

                Skip();
                if (Peek() == '(')
                {
                    // Variable definitions: their types don't matter here.
                    var depth = 0;
                    do
                    {
                        depth += text[_at] == '(' ? 1 : text[_at] == ')' ? -1 : 0;
                        _at++;
                    }
                    while (depth > 0);
                }
            }

            return Selection();
        }

        private List<GqlField> Selection()
        {
            Expect('{');
            var fields = new List<GqlField>();
            while (Skip() && Peek() != '}')
            {
                var name = Name();
                var key = name;
                Skip();
                if (Peek() == ':')
                {
                    _at++;
                    Skip();
                    name = Name();
                }

                var args = new JsonObject();
                Skip();
                if (Peek() == '(')
                {
                    _at++;
                    while (Skip() && Peek() != ')')
                    {
                        var arg = Name();
                        Skip();
                        Expect(':');
                        args[arg] = Value();
                    }

                    _at++;
                }

                Skip();
                fields.Add(new GqlField(key, name, args, Peek() == '{' ? Selection() : null));
            }

            _at++;
            return fields;
        }

        private JsonNode? Value()
        {
            Skip();
            var c = Peek();
            if (c == '$')
            {
                _at++;
                return variables[Name()]?.DeepClone();
            }

            if (c == '"')
            {
                var end = text.IndexOf('"', _at + 1);
                var s = text[(_at + 1)..end];
                _at = end + 1;
                return s;
            }

            if (c == '[')
            {
                _at++;
                var list = new JsonArray();
                while (Skip() && Peek() != ']')
                {
                    list.Add(Value());
                }

                _at++;
                return list;
            }

            if (c == '{')
            {
                _at++;
                var obj = new JsonObject();
                while (Skip() && Peek() != '}')
                {
                    var key = Name();
                    Skip();
                    Expect(':');
                    obj[key] = Value();
                }

                _at++;
                return obj;
            }

            if (char.IsDigit(c) || c == '-')
            {
                var start = _at++;
                while (_at < text.Length && (char.IsDigit(text[_at]) || text[_at] == '.'))
                {
                    _at++;
                }

                return JsonNode.Parse(text[start.._at]);
            }

            return Name() switch
            {
                "true" => true,
                "false" => false,
                "null" => null,
                var enumValue => enumValue,
            };
        }

        private string Name()
        {
            var start = _at;
            while (_at < text.Length && (char.IsLetterOrDigit(text[_at]) || text[_at] == '_'))
            {
                _at++;
            }

            return _at > start ? text[start.._at] : throw new FormatException($"Expected a name at {start}.");
        }

        private void Expect(char c)
        {
            Skip();
            if (Peek() != c)
            {
                throw new FormatException($"Expected '{c}' at {_at}.");
            }

            _at++;
        }

        private char Peek() => _at < text.Length ? text[_at] : throw new FormatException("Unexpected end of query.");

        /// <summary>Skips whitespace and commas (insignificant in GraphQL); always true, for use in loop conditions.</summary>
        private bool Skip()
        {
            while (_at < text.Length && (char.IsWhiteSpace(text[_at]) || text[_at] == ','))
            {
                _at++;
            }

            return true;
        }
    }
}

/// <summary>
/// An issue. <see cref="Change"/> logs a history entry in Linear's shape and applies it, so the issue and its history
/// agree; <see cref="Comment"/>, <see cref="Relate"/> and <see cref="Attach"/> add the rest.
/// </summary>
internal sealed class FakeIssue
{
    private readonly FakeLinear _linear;

    public FakeIssue(FakeLinear linear, string id, string identifier, JsonObject team, JsonObject state, string title, DateTimeOffset created, JsonObject? creator)
    {
        _linear = linear;
        Id = id;
        Json = new JsonObject
        {
            ["id"] = id,
            ["identifier"] = identifier,
            ["title"] = title,
            ["description"] = null,
            ["priority"] = 0,
            ["estimate"] = null,
            ["dueDate"] = null,
            ["url"] = $"https://linear.app/acme/issue/{identifier}/{title.ToLowerInvariant().Replace(' ', '-')}",
            ["createdAt"] = FakeLinear.Time(created),
            ["updatedAt"] = FakeLinear.Time(created),
            ["completedAt"] = null,
            ["canceledAt"] = null,
            ["archivedAt"] = null,
            ["trashed"] = null,
            ["labelIds"] = new JsonArray(),
            ["team"] = FakeLinear.Ref(team),
            ["state"] = FakeLinear.Ref(state),
            ["assignee"] = null,
            ["creator"] = creator is null ? null : FakeLinear.Ref(creator),
            ["parent"] = null,
            ["project"] = null,
            ["projectMilestone"] = null,
            ["cycle"] = null,
            ["relations"] = new JsonArray(),
            ["attachments"] = new JsonArray(),
            ["history"] = new JsonArray(),
        };
    }

    public string Id { get; }
    public JsonObject Json { get; }
    public string Identifier => Json["identifier"]!.GetValue<string>();

    public FakeIssue Set(string field, JsonNode? value)
    {
        Json[field] = value;
        return this;
    }

    private void Touch(DateTimeOffset at)
    {
        if (DateTimeOffset.Parse(Json["updatedAt"]!.GetValue<string>(), CultureInfo.InvariantCulture) < at)
        {
            Json["updatedAt"] = FakeLinear.Time(at);
        }
    }

    /// <summary>
    /// A history entry with the given <c>from…</c>/<c>to…</c> pairs (and <c>addedLabelIds</c>, <c>updatedDescription</c>),
    /// applied to the issue: the <c>to…</c> values become current.
    /// </summary>
    public FakeIssue Change(DateTimeOffset at, JsonObject? actor, JsonObject entry)
    {
        entry["id"] = _linear.NextId();
        entry["createdAt"] = FakeLinear.Time(at);
        entry["actorId"] = actor is null ? null : FakeLinear.Id(actor);
        Json["history"]!.AsArray().Add(entry);
        foreach (var (key, value) in entry.ToList())
        {
            switch (key)
            {
                case "toStateId": Json["state"] = Reference(value); break;
                case "toAssigneeId": Json["assignee"] = Reference(value); break;
                case "toCycleId": Json["cycle"] = Reference(value); break;
                case "toProjectId": Json["project"] = Reference(value); break;
                case "toParentId": Json["parent"] = Reference(value); break;
                case "toPriority": Json["priority"] = value?.DeepClone(); break;
                case "toEstimate": Json["estimate"] = value?.DeepClone(); break;
                case "toTitle": Json["title"] = value?.DeepClone(); break;
                case "toDueDate": Json["dueDate"] = value?.DeepClone(); break;
                case "addedLabelIds":
                    foreach (var label in value!.AsArray())
                    {
                        Json["labelIds"]!.AsArray().Add(label!.DeepClone());
                    }

                    break;
                case "removedLabelIds":
                    var labels = Json["labelIds"]!.AsArray();
                    foreach (var label in value!.AsArray())
                    {
                        labels.Remove(labels.First(l => l!.GetValue<string>() == label!.GetValue<string>()));
                    }

                    break;
            }
        }

        Touch(at);
        return this;

        static JsonObject? Reference(JsonNode? id) => id is null ? null : new JsonObject { ["id"] = id.DeepClone() };
    }

    /// <summary>A comment, or a reply when <paramref name="parent"/> is set. An edit moves <c>editedAt</c> and <c>updatedAt</c>.</summary>
    public JsonObject Comment(DateTimeOffset at, JsonObject? user, string body, JsonObject? parent = null, DateTimeOffset? edited = null)
    {
        var comment = new JsonObject
        {
            ["id"] = _linear.NextId(),
            ["body"] = body,
            ["createdAt"] = FakeLinear.Time(at),
            ["updatedAt"] = FakeLinear.Time(edited ?? at),
            ["editedAt"] = edited is { } e ? FakeLinear.Time(e) : null,
            ["archivedAt"] = null,
            ["issueId"] = Id,
            ["parentId"] = parent is null ? null : FakeLinear.Id(parent),
            ["user"] = user is null ? null : FakeLinear.Ref(user),
            ["issue"] = new JsonObject { ["id"] = Id, ["team"] = Json["team"]!.DeepClone() },
        };
        _linear.Comments.Add(comment);
        Touch(at);
        return comment;
    }

    public FakeIssue Relate(string type, FakeIssue other, DateTimeOffset at)
    {
        Json["relations"]!.AsArray().Add(new JsonObject
        {
            ["id"] = _linear.NextId(),
            ["type"] = type,
            ["relatedIssue"] = new JsonObject { ["id"] = other.Id },
            ["createdAt"] = FakeLinear.Time(at),
        });
        Touch(at);
        return this;
    }

    public FakeIssue Attach(DateTimeOffset at, JsonObject? by, string url, string? title, string sourceType = "upload")
    {
        Json["attachments"]!.AsArray().Add(new JsonObject
        {
            ["id"] = _linear.NextId(),
            ["url"] = url,
            ["title"] = title,
            ["subtitle"] = null,
            ["sourceType"] = sourceType,
            ["createdAt"] = FakeLinear.Time(at),
            ["creator"] = by is null ? null : FakeLinear.Ref(by),
        });
        Touch(at);
        return this;
    }
}
