using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kupro.Api.Features.Meetings;

namespace Kupro.Api.Features.Voice;

/// <summary>
/// Everything the interpreter knows when it reads one push-to-talk utterance. Stable parts first, so the vendor's prompt
/// cache can reuse them (ADR-015 IMP-006).
/// </summary>
/// <param name="Projects">"KEY: Name" for each project in the workspace.</param>
/// <param name="Project">Key of the project on screen, if any.</param>
/// <param name="CurrentIssue">The issue on screen: what "this one" means.</param>
public sealed record VoiceContext(
    string Language,
    IReadOnlyList<string> Projects,
    IReadOnlyList<string> Members,
    IReadOnlyList<string> Sprints,
    IReadOnlyList<IssueHint> Issues,
    string? Project,
    IssueHint? CurrentIssue,
    string Utterance);

/// <summary>A place heard in the utterance, before it is checked against the workspace. Empty strings mean "not said".</summary>
/// <param name="Sprint"><c>next</c>, <c>current</c> or a sprint name.</param>
public sealed record HeardTarget(VoiceView View, string Issue, string Project, string Assignee, string Sprint);

/// <param name="Usage">Null when no model was called, so nothing is metered.</param>
public sealed record VoiceReading(VoiceKind Kind, IReadOnlyList<ExtractedAction> Actions, HeardTarget? Navigate, ModelUsage? Usage);

/// <param name="Usage">Null when no model was called.</param>
public sealed record TidyResult(string Text, ModelUsage? Usage);

/// <summary>
/// Reads push-to-talk utterances and tidies dictation with a chat model (the <c>voice-commands</c> capability, ADR-022).
/// Which model, where and with whose key comes from <see cref="Ai.AiRouter"/>. An interface because it is an external
/// system that tests replace.
/// </summary>
public interface IVoiceInterpreter
{
    Task<VoiceReading> InterpretAsync(VoiceContext context, ModelRoute route, CancellationToken ct);

    /// <summary>The same words with spelling and punctuation fixed. The caller checks that nothing else changed.</summary>
    Task<TidyResult> TidyAsync(string text, string language, ModelRoute route, CancellationToken ct);
}

/// <summary>Chat completion with a JSON schema: the meeting operations plus navigation (task 036).</summary>
public sealed class OpenAiVoiceInterpreter(IHttpClientFactory clients) : IVoiceInterpreter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>A short utterance needs little room; the cap stops a model that pads the schema until the timeout.</summary>
    private const int MaxTokens = 2000;

    private static readonly JsonNode Schema = JsonNode.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["kind", "actions", "navigate"],
          "properties": {
            "kind": { "type": "string", "enum": ["operations", "navigate", "none"] },
            "actions": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["operation", "issue", "value", "confidence", "snippet"],
                "properties": {
                  "operation": { "type": "string", "enum": ["state", "sprint", "assignee", "estimate", "comment", "createIssue"] },
                  "issue": { "type": "string" },
                  "value": { "type": "string" },
                  "confidence": { "type": "number" },
                  "snippet": { "type": "string" }
                }
              }
            },
            "navigate": {
              "type": "object",
              "additionalProperties": false,
              "required": ["view", "issue", "project", "assignee", "sprint"],
              "properties": {
                "view": { "type": "string", "enum": ["none", "issue", "myIssues", "list", "board", "sprints", "plan"] },
                "issue": { "type": "string" },
                "project": { "type": "string" },
                "assignee": { "type": "string" },
                "sprint": { "type": "string" }
              }
            }
          }
        }
        """)!;

    private static readonly JsonNode TidySchema = JsonNode.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["text"],
          "properties": { "text": { "type": "string" } }
        }
        """)!;

    public async Task<VoiceReading> InterpretAsync(VoiceContext context, ModelRoute route, CancellationToken ct)
    {
        var (content, usage) = await CompleteAsync(route, Prompt, JsonSerializer.Serialize(context, Json), "voice", Schema, ct);
        if (Parse(content) is not JsonObject reading)
        {
            return new VoiceReading(VoiceKind.None, [], null, usage);
        }

        var kind = reading["kind"]?.GetValue<string>() switch
        {
            "operations" => VoiceKind.Operations,
            "navigate" => VoiceKind.Navigate,
            _ => VoiceKind.None,
        };
        var actions = reading["actions"] is JsonArray list
            ? list.OfType<JsonObject>()
                .Select(a => (Op: Enum.TryParse<ProposalOperation>(a["operation"]?.GetValue<string>(), true, out var op) ? op : (ProposalOperation?)null, A: a))
                .Where(x => x.Op is not null)
                .Select(x => new ExtractedAction(
                    x.Op!.Value,
                    x.A["issue"]?.GetValue<string>() ?? "",
                    x.A["value"]?.GetValue<string>().Trim().Trim('"'),
                    Math.Clamp(x.A["confidence"]?.GetValue<double>() ?? 0, 0, 1),
                    x.A["snippet"]?.GetValue<string>() ?? ""))
                .ToList()
            : [];
        HeardTarget? target = null;
        if (reading["navigate"] is JsonObject nav
            && Enum.TryParse<VoiceView>(nav["view"]?.GetValue<string>(), true, out var view))
        {
            string Field(string name) => nav[name]?.GetValue<string>().Trim() ?? "";
            target = new HeardTarget(view, Field("issue"), Field("project"), Field("assignee"), Field("sprint"));
        }

        return new VoiceReading(kind, actions, target, usage);
    }

    public async Task<TidyResult> TidyAsync(string text, string language, ModelRoute route, CancellationToken ct)
    {
        var user = JsonSerializer.Serialize(new { language, text }, Json);
        var (content, usage) = await CompleteAsync(route, TidyPrompt, user, "tidy", TidySchema, ct);
        return new TidyResult(Parse(content)?["text"]?.GetValue<string>().Trim() ?? text, usage);
    }

    private async Task<(string? Content, ModelUsage Usage)> CompleteAsync(ModelRoute route, string system, string user, string name,
        JsonNode schema, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = route.Model,
            ["temperature"] = 0,
            ["max_tokens"] = MaxTokens,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user },
            },
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = name, ["strict"] = true, ["schema"] = schema.DeepClone() },
            },
        };
        if (!string.IsNullOrWhiteSpace(route.ReasoningEffort))
        {
            payload["reasoning_effort"] = route.ReasoningEffort;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{route.Endpoint!.TrimEnd('/')}/chat/completions")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", route.ApiKey);
        using var response = await AiHttp.Client(clients, route).SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var completion = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        var content = completion?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        return (content, OpenAiProposalExtractor.Usage(completion?["usage"], system.Length + user.Length, content?.Length ?? 0));
    }

    /// <summary>Null when the output was cut off or isn't JSON: nothing understood rather than a failed request.</summary>
    private static JsonNode? Parse(string? content)
    {
        try
        {
            return content is null ? null : JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private const string Prompt = """
        You are the voice control of an issue tracker. A person pressed a button and said one short thing.
        The user message is JSON: the language, the workspace's projects ("KEY: Name"), people, sprints of the project on
        screen, its issues (key, title, state, assignee), the project and the issue on screen ("this one", "it"), and
        the utterance.
        Decide what the utterance asks for, exactly one kind:
        - "operations": change issues. Operations and their value:
          - state: backlog, todo, inProgress, inReview, done or canceled
          - sprint: next, current, none, or a sprint name
          - assignee: a person's name as listed, me, or none
          - estimate: hours, e.g. "16" (convert days at 8 hours)
          - comment: the note to add, in the utterance's language
          - createIssue: the new issue's title, in the utterance's language
          "issue" is the issue key when one is said or meant ("this one" is the issue on screen), otherwise the words
          used for it ("the login bug"); empty for createIssue. "snippet" quotes the words that triggered the action.
          "confidence" is 0-1: below 0.8 when the issue or the change is not certain.
        - "navigate": open a place. view: issue (with its key), myIssues, list or board (with a project key or name, and
          optionally a person's name as assignee), sprints (optionally sprint: next or current), plan.
        - "none": anything else, such as a question or chat.
        Fill unused fields with "" and use view "none" unless kind is navigate. Never invent keys, names or URLs.
        """;

    private const string TidyPrompt = """
        You fix dictated text. Correct spelling, capitalisation and punctuation only. Keep every word, its order and its
        meaning; never add, drop, reorder or replace words, and never change the language. Keep Markdown as it is.
        The user message is JSON with the language and the text. Return the fixed text.
        """;
}

/// <summary>
/// Offline reader for clear English and Danish commands, used when no model serves <c>voice-commands</c>, the workspace's
/// key was refused, or Kupro's AI budget is spent: changes come from the meeting agent's keyword rules, places from the
/// patterns below.
/// </summary>
public sealed partial class KeywordVoiceInterpreter
{
    private static readonly KeywordProposalExtractor Keywords = new();

    public async Task<VoiceReading> InterpretAsync(VoiceContext context, CancellationToken ct)
    {
        var utterance = context.CurrentIssue is { } current ? ThisOne().Replace(context.Utterance, current.Key, 1) : context.Utterance;
        var extraction = await Keywords.ExtractAsync(new ExtractionContext(
            context.Language, MeetingType.Other, context.Issues, context.Members, context.Sprints, [], utterance), ct);
        if (extraction.Actions.Count > 0)
        {
            return new VoiceReading(VoiceKind.Operations, extraction.Actions, null, null);
        }

        return Navigate(utterance) is { } target
            ? new VoiceReading(VoiceKind.Navigate, [], target, null)
            : new VoiceReading(VoiceKind.None, [], null, null);
    }

    private static HeardTarget? Navigate(string text)
    {
        if (OpenIssue().Match(text) is { Success: true } issue)
        {
            return new HeardTarget(VoiceView.Issue, issue.Groups["key"].Value.ToUpperInvariant(), "", "", "");
        }

        if (MyIssues().IsMatch(text))
        {
            return new HeardTarget(VoiceView.MyIssues, "", "", "", "");
        }

        if (Sprint().Match(text) is { Success: true } sprint)
        {
            var next = sprint.Groups["v"].Value.ToLowerInvariant() is "next" or "næste";
            return new HeardTarget(VoiceView.Sprints, "", "", "", next ? "next" : "current");
        }

        if (Board().Match(text) is { Success: true } board)
        {
            return new HeardTarget(VoiceView.Board, "", Clean(board.Groups["p"].Value), "", "");
        }

        if (FilterBy().Match(text) is { Success: true } filter)
        {
            return new HeardTarget(VoiceView.List, "", "", Clean(filter.Groups["a"].Value), "");
        }

        if (Plan().IsMatch(text))
        {
            return new HeardTarget(VoiceView.Plan, "", "", "", "");
        }

        return OpenProject().Match(text) is { Success: true } project
            ? new HeardTarget(VoiceView.List, "", Clean(project.Groups["p"].Value), "", "")
            : null;
    }

    private static string Clean(string value) => value.Trim().TrimEnd('.', '!', '?').Trim();

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>"this one", "den her": the issue on screen. Not "this sprint".</summary>
    [GeneratedRegex(@"\b(?:this(?:\s+(?:one|issue|ticket|task))?|it|den\s+her|det\s+her|denne(?:\s+(?:sag|opgave))?)\b(?!\s+sprint)", Options, 200)]
    private static partial Regex ThisOne();

    [GeneratedRegex(@"\b(?:open|show(?:\s+me)?|go\s+to|find|åbn|vis(?:\s+mig)?|gå\s+til)\s+(?:issue\s+|opgave\s+|sag\s+)?(?<key>[A-Z][A-Z0-9]{1,9}-\d+)\b", Options, 200)]
    private static partial Regex OpenIssue();

    [GeneratedRegex(@"\b(?:my\s+(?:issues|tasks|work)|mine\s+(?:opgaver|sager)|mit\s+arbejde)\b", Options, 200)]
    private static partial Regex MyIssues();

    [GeneratedRegex(@"\b(?:show|open|go\s+to|vis|åbn|gå\s+til)\s+(?:me\s+)?(?:the\s+)?(?<v>next|current|this|active|næste|nuværende|denne|aktive)\s+sprint", Options, 200)]
    private static partial Regex Sprint();

    [GeneratedRegex(@"\b(?:board|tavle(?:n)?)\s+(?:for|of|til|på|i)\s+(?:the\s+)?(?:project\s+|projekt(?:et)?\s+)?(?<p>[\p{L}\d][\p{L}\d -]{0,40}?)\s*(?:project)?[.!?]?$", Options, 200)]
    private static partial Regex Board();

    [GeneratedRegex(@"\b(?:filter|show|vis|filtrer)\s+(?:issues\s+)?(?:by|on|for|på|efter|opgaver\s+for)\s+(?<a>[\p{L}][\p{L} -]{0,40}?)[.!?]?$", Options, 200)]
    private static partial Regex FilterBy();

    [GeneratedRegex(@"\b(?:show|open|go\s+to|vis|åbn|gå\s+til)\s+(?:me\s+)?(?:the\s+)?(?:plan|roadmap|delivery\s+plan|planen|køreplanen)\b", Options, 200)]
    private static partial Regex Plan();

    [GeneratedRegex(@"\b(?:open|show|go\s+to|åbn|vis|gå\s+til)\s+(?:the\s+)?(?:project|projekt(?:et)?)\s+(?<p>[\p{L}\d][\p{L}\d -]{0,40}?)[.!?]?$", Options, 200)]
    private static partial Regex OpenProject();
}
