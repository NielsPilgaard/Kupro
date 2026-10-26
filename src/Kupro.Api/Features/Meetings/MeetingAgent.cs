using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Kupro.Api.Features.Meetings;

// Vendor seams for the meeting agent (ADR-015). Interfaces because both are external systems that tests replace.

/// <summary>An issue the agent may refer to, as the extractor sees it.</summary>
public sealed record IssueHint(string Key, string Title, string State, string? Assignee);

/// <summary>
/// Everything the extractor knows when it reads one utterance. Serialized in this order: what stays the same through a
/// meeting comes first, so the vendor's prompt cache can reuse it (ADR-015 IMP-006).
/// </summary>
/// <param name="Members">Display names people may be called by.</param>
/// <param name="Sprints">Names of the project's active and planned sprints.</param>
/// <param name="RecentTranscript">Earlier utterances, oldest first, for references like "that one".</param>
public sealed record ExtractionContext(
    string Language,
    MeetingType Type,
    IReadOnlyList<IssueHint> Issues,
    IReadOnlyList<string> Members,
    IReadOnlyList<string> Sprints,
    IReadOnlyList<string> RecentTranscript,
    string Utterance);

/// <summary>
/// One action heard, before it is matched to issues and members. Values: state <c>backlog|todo|inProgress|inReview|done|canceled</c>;
/// sprint <c>next|current|none</c> or a sprint name; assignee a person's name or <c>none</c>; estimate hours; comment
/// text; new issue title.
/// </summary>
/// <param name="IssueReference">Issue key, or the words used for it ("the login bug"); empty for a new issue.</param>
public sealed record ExtractedAction(ProposalOperation Operation, string IssueReference, string? Value, double Confidence, string Snippet);

/// <summary>Tokens one model call used, from the response's <c>usage</c> (task 017).</summary>
/// <param name="CachedInputTokens">Part of <paramref name="InputTokens"/> the vendor served from its prompt cache.</param>
public sealed record ModelUsage(long InputTokens, long CachedInputTokens, long OutputTokens);

/// <param name="Usage">Null when no model was called (keyword matching), so nothing is metered.</param>
public sealed record Extraction(IReadOnlyList<ExtractedAction> Actions, ModelUsage? Usage);

/// <param name="AudioSeconds">Length of the audio sent, which the vendor bills.</param>
public sealed record Transcription(string Text, double AudioSeconds);

public interface IProposalExtractor
{
    Task<Extraction> ExtractAsync(ExtractionContext context, CancellationToken ct);
}

public interface ISpeechToText
{
    /// <summary>False when no model is configured for the language; the browser then can't stream audio.</summary>
    bool Supports(string language);

    Task<Transcription> TranscribeAsync(Stream wav, string language, CancellationToken ct);
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

    /// <summary>Monthly AI budget per workspace (ADR-013, task 017). Not set means no limits, as for self-hosters.</summary>
    public MeetingBudgetOptions? Budget { get; set; }
}

/// <summary>
/// The allowance in meeting hours and what one hour is worth. Usage is metered in euro at each route's rates, so the
/// margin holds whatever the mix of speech and typed text (ADR-013).
/// </summary>
public sealed class MeetingBudgetOptions
{
    /// <summary>Hours per seat a month, pooled across the workspace.</summary>
    public decimal HoursPerSeat { get; set; } = 2;

    /// <summary>Euro value of one meeting hour, which turns hours into a budget (the pricing calculator's hour cost).</summary>
    public decimal EuroPerHour { get; set; }

    /// <summary>Server-wide spend per day (Copenhagen) after which every workspace falls back as if over budget.</summary>
    public decimal? DailyCeilingEuro { get; set; }
}

public sealed class ModelRoute
{
    /// <summary>OpenAI-compatible base URL, e.g. <c>https://api.scaleway.ai/v1</c>.</summary>
    public string? Endpoint { get; set; }

    public string? Model { get; set; }
    public string? ApiKey { get; set; }

    /// <summary>Chat only: <c>low</c>, <c>medium</c> or <c>high</c> for a reasoning model; unset sends nothing.</summary>
    public string? ReasoningEffort { get; set; }

    // The vendor's rates for this route's model (task 017): a model change is a config change, rates included.

    /// <summary>Transcription: euro per minute of audio.</summary>
    public decimal? EuroPerAudioMinute { get; set; }

    /// <summary>Extraction: euro per million input tokens.</summary>
    public decimal? EuroPerMillionInputTokens { get; set; }

    /// <summary>Extraction: euro per million cached input tokens; unset means the input rate.</summary>
    public decimal? EuroPerMillionCachedInputTokens { get; set; }

    /// <summary>Extraction: euro per million output tokens.</summary>
    public decimal? EuroPerMillionOutputTokens { get; set; }

    public Dictionary<string, ModelRoute> Languages { get; set; } = [];

    public ModelRoute For(string language) =>
        Languages.TryGetValue(language, out var route)
            ? new ModelRoute
            {
                Endpoint = route.Endpoint ?? Endpoint,
                Model = route.Model ?? Model,
                ApiKey = route.ApiKey ?? ApiKey,
                ReasoningEffort = route.ReasoningEffort ?? ReasoningEffort,
                EuroPerAudioMinute = route.EuroPerAudioMinute ?? EuroPerAudioMinute,
                EuroPerMillionInputTokens = route.EuroPerMillionInputTokens ?? EuroPerMillionInputTokens,
                EuroPerMillionCachedInputTokens = route.EuroPerMillionCachedInputTokens ?? EuroPerMillionCachedInputTokens,
                EuroPerMillionOutputTokens = route.EuroPerMillionOutputTokens ?? EuroPerMillionOutputTokens,
            }
            : this;

    /// <summary>All three set. Production always passes the key variable, so an empty key means "not set up yet".</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Model) && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>Whisper-style <c>/audio/transcriptions</c> (Scaleway, ADR-015).</summary>
public sealed class OpenAiSpeechToText(HttpClient http, IOptions<MeetingAgentOptions> options) : ISpeechToText
{
    public bool Supports(string language) => options.Value.Transcription.For(language).IsConfigured;

    public async Task<Transcription> TranscribeAsync(Stream wav, string language, CancellationToken ct)
    {
        var route = options.Value.Transcription.For(language);
        // Under 2 MB (the endpoint's limit): buffered so its length can be metered before it goes out.
        using var audio = new MemoryStream();
        await wav.CopyToAsync(audio, ct);
        var seconds = Wav.Seconds(audio.GetBuffer().AsSpan(0, (int)audio.Length));
        audio.Position = 0;
        using var form = new MultipartFormDataContent
        {
            { new StreamContent(audio) { Headers = { ContentType = new MediaTypeHeaderValue("audio/wav") } }, "file", "utterance.wav" },
            { new StringContent(route.Model!), "model" },
            { new StringContent(language), "language" },
            { new StringContent("json"), "response_format" },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{route.Endpoint!.TrimEnd('/')}/audio/transcriptions") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", route.ApiKey);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        return new Transcription(body?["text"]?.GetValue<string>().Trim() ?? "", seconds);
    }
}

/// <summary>Length of a PCM WAV from its header, for metering (task 017).</summary>
public static class Wav
{
    /// <summary>The browser's format (16 kHz mono 16-bit, 32,000 bytes a second) when the header can't be read.</summary>
    private const int DefaultByteRate = 32_000;

    public static double Seconds(ReadOnlySpan<byte> wav)
    {
        var byteRate = 0;
        var dataBytes = -1L;
        if (wav.Length >= 12 && wav[..4].SequenceEqual("RIFF"u8) && wav[8..12].SequenceEqual("WAVE"u8))
        {
            // Walk the chunks: "fmt " carries the byte rate, "data" the samples.
            for (var offset = 12; offset + 8 <= wav.Length;)
            {
                var id = wav.Slice(offset, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(offset + 4, 4));
                if (id.SequenceEqual("fmt "u8) && offset + 20 <= wav.Length)
                {
                    byteRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(offset + 16, 4));
                }
                else if (id.SequenceEqual("data"u8))
                {
                    dataBytes = Math.Min(size, wav.Length - offset - 8);
                    break;
                }

                var next = offset + 8L + size + (size & 1);
                if (next > wav.Length)
                {
                    break;
                }

                offset = (int)next;
            }
        }

        return (double)(dataBytes >= 0 ? dataBytes : wav.Length) / (byteRate > 0 ? byteRate : DefaultByteRate);
    }
}

/// <summary>Chat completion with a JSON schema response (ADR-015). Issue and member matching stays in our code.</summary>
public sealed class OpenAiProposalExtractor(HttpClient http, IOptions<MeetingAgentOptions> options) : IProposalExtractor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Room for a reasoning model's thinking (up to ~2,000 tokens in the ADR-015 spike) plus a few actions; the cap stops a
    /// model that pads the schema with whitespace until the timeout.
    /// </summary>
    private const int MaxTokens = 4000;

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

    public async Task<Extraction> ExtractAsync(ExtractionContext context, CancellationToken ct)
    {
        var route = options.Value.Extraction.For(context.Language);
        var user = JsonSerializer.Serialize(context, Json);
        var payload = new JsonObject
        {
            ["model"] = route.Model,
            ["temperature"] = 0,
            ["max_tokens"] = MaxTokens,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = Prompt },
                new JsonObject { ["role"] = "user", ["content"] = user },
            },
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "proposals", ["strict"] = true, ["schema"] = Schema.DeepClone() },
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
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var completion = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        var content = completion?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        var usage = Usage(completion?["usage"], Prompt.Length + user.Length, content?.Length ?? 0);
        if (content is null || Parse(content)?["actions"] is not JsonArray actions)
        {
            return new Extraction([], usage);
        }

        return new Extraction(actions.OfType<JsonObject>()
            .Select(a => (Op: Enum.TryParse<ProposalOperation>(a["operation"]?.GetValue<string>(), true, out var op) ? op : (ProposalOperation?)null, A: a))
            .Where(x => x.Op is not null)
            .Select(x => new ExtractedAction(
                x.Op!.Value,
                x.A["issue"]?.GetValue<string>() ?? "",
                x.A["value"]?.GetValue<string>().Trim().Trim('"'),
                Math.Clamp(x.A["confidence"]?.GetValue<double>() ?? 0, 0, 1),
                x.A["snippet"]?.GetValue<string>() ?? ""))
            .ToList(), usage);
    }

    /// <summary>
    /// The response's token counts. Without them (not every OpenAI-compatible vendor sends <c>usage</c>), an estimate
    /// at four characters a token, so the budget still moves.
    /// </summary>
    private static ModelUsage Usage(JsonNode? usage, int promptChars, int outputChars)
    {
        long? Read(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var n) ? n : null;
        var input = Read(usage?["prompt_tokens"]);
        var output = Read(usage?["completion_tokens"]);
        var cached = Read(usage?["prompt_tokens_details"]?["cached_tokens"]) ?? 0;
        return input is null || output is null
            ? new ModelUsage(promptChars / CharsPerToken, 0, Math.Max(outputChars / CharsPerToken, 1))
            : new ModelUsage(input.Value, Math.Min(cached, input.Value), output.Value);
    }

    private const int CharsPerToken = 4;

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
        The user message is JSON: the meeting language and type, the project's issues (key, title, state, assignee),
        the people, the sprints, the recent transcript, and the newest utterance.
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
/// Offline extractor for clear English and Danish commands ("move KUP-12 to next sprint", "KUP-3 er færdig"). Used
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

    public Task<Extraction> ExtractAsync(ExtractionContext context, CancellationToken ct)
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

        // No model, so nothing to meter.
        return Task.FromResult(new Extraction(actions, Usage: null));
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
