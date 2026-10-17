using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Flowstate.Api.Features.Meetings;

// Vendor seams for the meeting agent (ADR-015). Interfaces because both are external systems that tests replace.

/// <summary>An issue the agent may refer to, as the extractor sees it.</summary>
public sealed record IssueHint(string Key, string Title, string State, string? Assignee);

/// <summary>Everything the extractor knows when it reads one utterance.</summary>
/// <param name="RecentTranscript">Earlier utterances, oldest first, for references like "that one".</param>
/// <param name="Members">Display names people may be called by.</param>
/// <param name="Sprints">Names of the project's active and planned sprints.</param>
public sealed record ExtractionContext(
    string Language,
    MeetingType Type,
    IReadOnlyList<string> RecentTranscript,
    string Utterance,
    IReadOnlyList<IssueHint> Issues,
    IReadOnlyList<string> Members,
    IReadOnlyList<string> Sprints);

/// <summary>
/// One action heard, before it is matched to issues and members. Values: state <c>backlog|todo|inProgress|inReview|done|canceled</c>;
/// sprint <c>next|current|none</c> or a sprint name; assignee a person's name or <c>none</c>; estimate hours; comment
/// text; new issue title.
/// </summary>
/// <param name="IssueReference">Issue key, or the words used for it ("the login bug"); empty for a new issue.</param>
public sealed record ExtractedAction(ProposalOperation Operation, string IssueReference, string? Value, double Confidence, string Snippet);

public interface IProposalExtractor
{
    Task<IReadOnlyList<ExtractedAction>> ExtractAsync(ExtractionContext context, CancellationToken ct);
}

public interface ISpeechToText
{
    /// <summary>False when no model is configured for the language; the browser then can't stream audio.</summary>
    bool Supports(string language);

    Task<string> TranscribeAsync(Stream wav, string language, CancellationToken ct);
}

/// <summary>
/// Model routes per seam (ADR-015 decision 4). A language entry overrides the default route; fields it leaves empty
/// (often the API key) come from the default.
/// </summary>
public sealed class MeetingAgentOptions
{
    public const string Section = "MeetingAgent";

    public ModelRoute Transcription { get; set; } = new();
    public ModelRoute Extraction { get; set; } = new();
}

public sealed class ModelRoute
{
    /// <summary>OpenAI-compatible base URL, e.g. <c>https://api.scaleway.ai/v1</c>.</summary>
    public string? Endpoint { get; set; }

    public string? Model { get; set; }
    public string? ApiKey { get; set; }
    public Dictionary<string, ModelRoute> Languages { get; set; } = [];

    public ModelRoute For(string language) =>
        Languages.TryGetValue(language, out var route)
            ? new ModelRoute { Endpoint = route.Endpoint ?? Endpoint, Model = route.Model ?? Model, ApiKey = route.ApiKey ?? ApiKey }
            : this;

    /// <summary>All three set. Production always passes the key variable, so an empty key means "not set up yet".</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Model) && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>Whisper-style <c>/audio/transcriptions</c> (Scaleway, ADR-015).</summary>
public sealed class OpenAiSpeechToText(HttpClient http, IOptions<MeetingAgentOptions> options) : ISpeechToText
{
    public bool Supports(string language) => options.Value.Transcription.For(language).IsConfigured;

    public async Task<string> TranscribeAsync(Stream wav, string language, CancellationToken ct)
    {
        var route = options.Value.Transcription.For(language);
        using var form = new MultipartFormDataContent
        {
            { new StreamContent(wav) { Headers = { ContentType = new MediaTypeHeaderValue("audio/wav") } }, "file", "utterance.wav" },
            { new StringContent(route.Model!), "model" },
            { new StringContent(language), "language" },
            { new StringContent("json"), "response_format" },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{route.Endpoint!.TrimEnd('/')}/audio/transcriptions") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", route.ApiKey);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        return body?["text"]?.GetValue<string>().Trim() ?? "";
    }
}

/// <summary>Chat completion with a JSON schema response (ADR-015). Issue and member matching stays in our code.</summary>
public sealed class OpenAiProposalExtractor(HttpClient http, IOptions<MeetingAgentOptions> options) : IProposalExtractor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>A few actions fit easily; the cap stops a model that pads the schema with whitespace until the timeout.</summary>
    private const int MaxTokens = 1000;

    private static readonly JsonNode Schema = JsonNode.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["actions"],
          "properties": {
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
            }
          }
        }
        """)!;

    public async Task<IReadOnlyList<ExtractedAction>> ExtractAsync(ExtractionContext context, CancellationToken ct)
    {
        var route = options.Value.Extraction.For(context.Language);
        var payload = new JsonObject
        {
            ["model"] = route.Model,
            ["temperature"] = 0,
            ["max_tokens"] = MaxTokens,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = Prompt },
                new JsonObject { ["role"] = "user", ["content"] = JsonSerializer.Serialize(context, Json) },
            },
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "proposals", ["strict"] = true, ["schema"] = Schema.DeepClone() },
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{route.Endpoint!.TrimEnd('/')}/chat/completions")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", route.ApiKey);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var completion = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        var content = completion?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        if (content is null || Parse(content)?["actions"] is not JsonArray actions)
        {
            return [];
        }

        return actions.OfType<JsonObject>()
            .Select(a => (Op: Enum.TryParse<ProposalOperation>(a["operation"]?.GetValue<string>(), true, out var op) ? op : (ProposalOperation?)null, A: a))
            .Where(x => x.Op is not null)
            .Select(x => new ExtractedAction(
                x.Op!.Value,
                x.A["issue"]?.GetValue<string>() ?? "",
                x.A["value"]?.GetValue<string>().Trim().Trim('"'),
                Math.Clamp(x.A["confidence"]?.GetValue<double>() ?? 0, 0, 1),
                x.A["snippet"]?.GetValue<string>() ?? ""))
            .ToList();
    }

    /// <summary>Null when the output was cut off at <see cref="MaxTokens"/>: no proposals rather than a failed transcript.</summary>
    private static JsonNode? Parse(string content)
    {
        try
        {
            return JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private const string Prompt = """
        You listen to a software team's meeting and propose changes to their issue tracker.
        The user message is JSON: the meeting language and type, the recent transcript, the newest utterance, the
        project's issues (key, title, state, assignee), the people, and the sprints.
        Propose actions only for what the newest utterance clearly decides or asks for, never for mere discussion.
        Operations and their value:
        - state: backlog, todo, inProgress, inReview, done or canceled
        - sprint: next, current, none, or a sprint name
        - assignee: a person's name as listed, or none
        - estimate: hours, e.g. "value": "16" (convert days at 8 hours)
        - comment: the note to add, in the meeting's language
        - createIssue: the new issue's title, in the meeting's language
        "issue" is the issue key when one is said or obvious from the list, otherwise the words used for it
        ("the login bug"); empty for createIssue. "snippet" quotes the words that triggered the action.
        "confidence" is 0-1: below 0.8 when the issue or the decision is not certain.
        Return {"actions": []} when nothing was decided.
        """;
}

/// <summary>
/// Offline extractor for clear English and Danish commands ("move FLOW-12 to next sprint", "FLOW-3 er færdig"). Used
/// when no LLM is configured, so dev, E2E and self-hosters without a model still get proposals.
/// </summary>
public sealed partial class KeywordProposalExtractor : IProposalExtractor
{
    private const string Ref = @"(?<ref>[A-Z][A-Z0-9]{1,9}-\d+|(?:the|den|det)\s+[\p{L}\d][\p{L}\d\s-]{1,40}?)";

    private static readonly (ProposalOperation Op, Regex Pattern)[] Rules =
    [
        (ProposalOperation.Sprint, Rule($@"\b(?:move|put|push)\s+{Ref}\s+(?:in)?to\s+(?:the\s+)?(?<v>next|current|this|næste|nuværende|denne)\s+sprint")),
        (ProposalOperation.Sprint, Rule($@"\b(?:flyt(?:ter)?|ryk(?:ker)?|skub(?:ber)?)\s+{Ref}\s+til\s+(?<v>næste|nuværende|denne)\s+sprint")),
        (ProposalOperation.State, Rule($@"\b(?:move|set|put)\s+{Ref}\s+(?:in)?to\s+(?<v>backlog|to ?do|in progress|in review|review|done)\b")),
        (ProposalOperation.State, Rule($@"\b(?:flyt(?:ter)?|sæt(?:ter)?|ryk(?:ker)?)\s+{Ref}\s+til\s+(?<v>backlog|to ?do|i gang|igang|review|færdig|done)\b")),
        (ProposalOperation.State, Rule($@"{Ref}\s+(?:is|er)\s+(?:now\s+|nu\s+)?(?<v>done|finished|complete|completed|in review|in progress|færdig|lukket|i gang|i review)\b")),
        (ProposalOperation.State, Rule($@"\b(?<v>close|finish|cancel|start(?:ed)?(?:\s+working)?\s+on|luk|afslut|annuller|start(?:er)?\s+på)\s+{Ref}")),
        (ProposalOperation.Assignee, Rule($@"\b(?:assign|tildel(?:er)?|giv(?:er)?)\s+{Ref}\s+(?:to|til)\s+(?<v>[\p{{L}}][\p{{L}} -]{{0,40}}?)(?=[.,;!?]|$|\s+(?:and|og)\s)")),
        (ProposalOperation.Assignee, Rule($@"\b(?<v>[\p{{Lu}}][\p{{L}}]+)\s+(?:will take|takes|is taking|tager)\s+{Ref}")),
        (ProposalOperation.Estimate, Rule($@"\b(?:estimate|size|estimer(?:er)?|vurder(?:er)?)\s+{Ref}\s+(?:at|to|as|til)?\s*(?<n>\d+(?:[.,]\d+)?)\s*(?<u>hours?|h|days?|d|timer?|t|dage?)\b")),
        (ProposalOperation.Comment, Rule($@"\b(?:comment on|note on|add a note to|kommentar til|note til)\s+{Ref}\s*[:,–-]\s*(?<v>[^.!?\n]+)")),
        (ProposalOperation.CreateIssue, Rule(@"\b(?:create|add|open|opret|lav|tilføj)\s+(?:a\s+|an\s+|en\s+)?(?:new\s+|ny\s+)?(?:issue|task|ticket|bug|story|opgave|sag|fejl)\s+(?:for|to|about|om|til|:)\s*(?<v>[^.!?\n]+)")),
    ];

    public Task<IReadOnlyList<ExtractedAction>> ExtractAsync(ExtractionContext context, CancellationToken ct)
    {
        var actions = new List<ExtractedAction>();
        foreach (var (op, pattern) in Rules)
        {
            foreach (Match m in pattern.Matches(context.Utterance))
            {
                var reference = m.Groups["ref"].Success ? m.Groups["ref"].Value.Trim() : "";
                var byKey = IssueKey().IsMatch(reference.ToUpperInvariant());
                reference = byKey ? reference.ToUpperInvariant() : reference;
                if (Value(op, m) is not { } value || actions.Any(a => a.Operation == op && a.IssueReference == reference))
                {
                    continue;
                }

                actions.Add(new ExtractedAction(op, reference, value, byKey || op == ProposalOperation.CreateIssue ? 0.95 : 0.6, m.Value.Trim()));
            }
        }

        return Task.FromResult<IReadOnlyList<ExtractedAction>>(actions);
    }

    private static string? Value(ProposalOperation op, Match m)
    {
        var v = m.Groups["v"].Value.Trim().TrimEnd('.', '!', '?');
        return op switch
        {
            ProposalOperation.Sprint => v.ToLowerInvariant() is "next" or "næste" ? "next" : "current",
            ProposalOperation.State => StateWord(v.ToLowerInvariant()),
            ProposalOperation.Estimate => Hours(m.Groups["n"].Value, m.Groups["u"].Value),
            _ => v.Length > 0 ? v : null,
        };
    }

    private static string? StateWord(string v) => v switch
    {
        "backlog" => "backlog",
        "todo" or "to do" => "todo",
        "in progress" or "i gang" or "igang" => "inProgress",
        "review" or "in review" or "i review" => "inReview",
        "done" or "finished" or "complete" or "completed" or "færdig" or "lukket" or "close" or "finish" or "luk" or "afslut" => "done",
        "cancel" or "annuller" => "canceled",
        _ when v.StartsWith("start", StringComparison.Ordinal) => "inProgress",
        _ => null,
    };

    private static string? Hours(string number, string unit)
    {
        if (!decimal.TryParse(number.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
        {
            return null;
        }

        var days = unit.StartsWith('d');
        return (days ? n * 8 : n).ToString(CultureInfo.InvariantCulture);
    }

    private static Regex Rule(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    [GeneratedRegex(@"^[A-Z][A-Z0-9]{1,9}-\d+$")]
    private static partial Regex IssueKey();
}
