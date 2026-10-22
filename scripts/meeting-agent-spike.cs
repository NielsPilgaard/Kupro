#:project ../src/Kupro.Api/Kupro.Api.csproj
#:property NoWarn=$(NoWarn);CA1050;CA2007;CA1303;CA1305;CA1307;CA1310;CA1849;CA1031;CA2000;CA1852;IDE0005;IL2026;IL3050
#:property TreatWarningsAsErrors=false
#:property PublishAot=false
// Meeting agent extraction spike (ADR-015 IMP-006): typed Danish/English utterances through the production
// OpenAiProposalExtractor, scored for precision/recall, latency and token use. Money is the pricing skill's job
// (.claude/skills/pricing), so this prints tokens, not euros.
//
//   dotnet run scripts/meeting-agent-spike.cs                      # production model, production reasoning effort
//   dotnet run scripts/meeting-agent-spike.cs -- glm-5.3 gpt-oss-120b --effort none
//
// Key: SCALEWAY_AI_KEY, else Parameters:scaleway-ai-key in the AppHost user-secrets. One run is ~30 calls per model,
// a few cents. If Kupro.Api.exe is locked by `aspire run`, add --property:OutDir=<temp dir>.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kupro.Api.Features.Meetings;
using Microsoft.Extensions.Options;

var effortAt = Array.IndexOf(args, "--effort");
var effort = effortAt >= 0 && effortAt + 1 < args.Length ? args[effortAt + 1] : "low";
var models = args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (effortAt < 0 || i != effortAt + 1)).ToArray();
if (models.Length == 0)
{
    models = ["deepseek-v4.1-flash"];
}

var key = Environment.GetEnvironmentVariable("SCALEWAY_AI_KEY") is { Length: > 0 } fromEnv ? fromEnv : UserSecret("kupro-apphost", "Parameters:scaleway-ai-key")
    ?? throw new InvalidOperationException("Set SCALEWAY_AI_KEY or `dotnet user-secrets set Parameters:scaleway-ai-key <key>` in src/Kupro.AppHost.");

IssueHint[] issues =
[
    new("KUP-12", "Login fails with SSO on Safari", "inProgress", "Mette Hansen"),
    new("KUP-14", "Export workspace to JSON", "todo", "Jonas Nielsen"),
    new("KUP-15", "Dark mode for the board", "backlog", null),
    new("KUP-18", "Slow search on large workspaces", "inReview", "Ahmed Khan"),
    new("KUP-21", "Onboarding emails in Danish", "todo", "Sofie Larsen"),
    new("KUP-23", "Payment page crashes on mobile", "todo", null),
    new("KUP-27", "Rate limiting for the public API", "backlog", null),
    new("KUP-30", "Upgrade to .NET 10", "done", "Jonas Nielsen"),
];
string[] members = ["Mette Hansen", "Jonas Nielsen", "Ahmed Khan", "Sofie Larsen", "Niels"];
string[] sprints = ["Sprint 14 (current)", "Sprint 15 (next)"];

static Exp E(ProposalOperation op, string issue, params string[] values) => new(op, issue, values);
var S = MeetingType.Standup; var P = MeetingType.Planning; var R = MeetingType.Refinement;
var ops = (State: ProposalOperation.State, Sprint: ProposalOperation.Sprint, Assignee: ProposalOperation.Assignee,
    Estimate: ProposalOperation.Estimate, Comment: ProposalOperation.Comment, Create: ProposalOperation.CreateIssue);
string[] next = ["next", "Sprint 15"]; string[] current = ["current", "Sprint 14"];

Case[] cases =
[
    // English
    new("en-clear-sprint", "en", P, [], "Okay, let's move KUP-12 to the next sprint.", [E(ops.Sprint, "KUP-12", next)]),
    new("en-clear-done", "en", S, [], "KUP-18 is done, Ahmed merged it this morning.", [E(ops.State, "KUP-18", "done")]),
    new("en-future-noop", "en", S, [], "The Safari login thing, I'm still on it, should be ready for review by tomorrow.", []),
    new("en-assign-by-title", "en", P, [], "Let's give the mobile payment crash to Jonas.", [E(ops.Assignee, "KUP-23", "Jonas")]),
    new("en-estimate-days", "en", R, [], "Dark mode, I'd say two days of work. Let's put that down.", [E(ops.Estimate, "KUP-15", "16")]),
    new("en-cancel", "en", P, [], "We don't need rate limiting before launch, let's just drop that ticket.", [E(ops.State, "KUP-27", "canceled")]),
    new("en-comment", "en", S, [], "Add a note on the search one: it's only slow above ten thousand issues.", [E(ops.Comment, "KUP-18", "*")]),
    new("en-create", "en", S, [], "We need a ticket for the CSV import failing on semicolons.", [E(ops.Create, "", "*")]),
    new("en-discussion-noop", "en", S, [], "Has anyone looked at why the build was red yesterday?", []),
    new("en-anaphora", "en", S, ["We shipped the onboarding emails yesterday."], "So that one's done as well.", [E(ops.State, "KUP-21", "done")]),
    new("en-multi", "en", P, [], "Move dark mode into this sprint and give it to Sofie.", [E(ops.Sprint, "KUP-15", current), E(ops.Assignee, "KUP-15", "Sofie")]),
    new("en-unassign", "en", S, [], "Ahmed is on holiday for two weeks, so take him off the search issue for now.", [E(ops.Assignee, "KUP-18", "none")]),
    new("en-stt-key", "en", S, [], "kup twenty one is done.", [E(ops.State, "KUP-21", "done")]),
    new("en-two-issues", "en", P, [], "Push the export and the onboarding emails to sprint 15.", [E(ops.Sprint, "KUP-14", next), E(ops.Sprint, "KUP-21", next)]),
    new("en-negation-noop", "en", P, [], "No, don't move KUP-12, leave it where it is.", []),
    // Danish
    new("da-clear-sprint", "da", P, [], "Okay, vi flytter KUP-12 til næste sprint.", [E(ops.Sprint, "KUP-12", next)]),
    new("da-clear-done", "da", S, [], "KUP-14 er færdig, den blev merget i går.", [E(ops.State, "KUP-14", "done")]),
    new("da-review-by-title", "da", S, [], "Login-fejlen på Safari, den er i review nu.", [E(ops.State, "KUP-12", "inReview")]),
    new("da-assign", "da", P, ["Kan Sofie tage betalingsfejlen på mobil?"], "Ja, det gør hun, giv den til hende.", [E(ops.Assignee, "KUP-23", "Sofie")]),
    new("da-estimate", "da", R, [], "Dark mode er nok en tre timers opgave, skriv det på.", [E(ops.Estimate, "KUP-15", "3")]),
    new("da-create", "da", S, [], "Vi skal have en opgave om at CSV-importen fejler med semikolon.", [E(ops.Create, "", "*")]),
    new("da-question-noop", "da", S, [], "Hvad med eksporten, er der nogen der ved hvor langt den er?", []),
    new("da-codeswitch-cancel", "da", P, [], "Den der rate limiting story, den skal vi bare cancel'e.", [E(ops.State, "KUP-27", "canceled")]),
    new("da-comment", "da", S, [], "Skriv en kommentar på søgningen: det er kun langsomt over ti tusind sager.", [E(ops.Comment, "KUP-18", "*")]),
    new("da-start", "da", S, ["Jonas, kan du starte på eksporten i dag?"], "Jo, jeg går i gang med den i dag.", [E(ops.State, "KUP-14", "inProgress")]),
    new("da-multi", "da", P, [], "Lad os tage dark mode ind i den her sprint og give den til Ahmed.", [E(ops.Sprint, "KUP-15", current), E(ops.Assignee, "KUP-15", "Ahmed")]),
    new("da-stt-key", "da", S, [], "kup 12 er færdig nu.", [E(ops.State, "KUP-12", "done")]),
    new("da-discussion-noop", "da", R, [], "Jeg synes vi skal snakke om, om vi overhovedet skal have dark mode.", []),
    new("da-unassign", "da", S, [], "Mette er syg resten af ugen, så tag hende af Safari-sagen.", [E(ops.Assignee, "KUP-12", "none")]),
    // "sæt det på" could also mean "add it to the sprint"; a sprint guess here is a weak miss, not a dangerous one.
    new("da-estimate-day", "da", R, [], "Betalingssiden, det er en dags arbejde, sæt det på.", [E(ops.Estimate, "KUP-23", "8")]),
];

var titles = issues.ToDictionary(i => i.Key, i => i.Title);
// Production sends up to 60 active issues, sorted by key (MeetingService.ContextAsync), so pad to 60 for real token counts.
string[] filler = ["Audit log retention setting", "Keyboard shortcut for new issue", "Webhook retries", "Avatar upload size limit",
    "Sprint burndown chart", "Bulk edit labels", "Markdown tables in comments", "Invite link expiry", "Board swimlanes by assignee",
    "CLI shell completion", "MCP tool for sprint planning", "Email digest of mentions", "Issue templates per project",
    "Rename a project key", "Search by label", "Time zone in due dates"];
var hints = issues.Concat(Enumerable.Range(0, 52).Select(i => new IssueHint($"KUP-{40 + i}",
    $"{filler[i % filler.Length]}{(i >= filler.Length ? $" ({i / filler.Length + 1})" : "")}", i % 3 == 0 ? "todo" : "backlog", null)))
    .OrderBy(h => h.Key, StringComparer.Ordinal).ToList();

var results = new ConcurrentBag<ModelResult>();
await Parallel.ForEachAsync(models, new ParallelOptions { MaxDegreeOfParallelism = 6 }, async (model, ct) =>
{
    var usage = new UsageHandler { InnerHandler = new HttpClientHandler() };
    using var http = new HttpClient(usage) { Timeout = TimeSpan.FromSeconds(60) };
    var options = Options.Create(new MeetingAgentOptions
    {
        Extraction = new ModelRoute
        {
            Endpoint = "https://api.scaleway.ai/v1", Model = model, ApiKey = key, ReasoningEffort = effort == "none" ? null : effort,
        },
    });
    var extractor = new OpenAiProposalExtractor(http, options);
    var rows = new List<CaseResult>();
    // Grouped like a real meeting (one language, one type), so the prompt cache behaves as it would in production.
    foreach (var c in cases.OrderBy(c => c.Language).ThenBy(c => c.Type))
    {
        var context = new ExtractionContext(c.Language, c.Type, hints, members, sprints, c.Recent, c.Utterance);
        var sw = Stopwatch.StartNew();
        IReadOnlyList<ExtractedAction> actions;
        string? error = null;
        try
        {
            actions = await extractor.ExtractAsync(context, ct);
        }
        catch (Exception ex)
        {
            actions = [];
            error = ex.Message;
        }

        sw.Stop();
        var call = usage.Take();
        rows.Add(Score(c, actions, sw.ElapsedMilliseconds, call, error ?? (call.FinishReason == "length" ? "max_tokens" : null), titles));
    }

    results.Add(new ModelResult(model, rows));
});

Console.WriteLine($"reasoning effort: {effort}");
Console.WriteLine($"{"model",-36} {"prec",5} {"recall",6} {"exact",5} {"confWrong",9} {"p50ms",6} {"p95ms",6} {"tokIn",6} {"cached",6} {"tokOut",6} {"err",3}");
foreach (var r in results.OrderByDescending(r => r.F1))
{
    Console.WriteLine($"{r.Model,-36} {r.Precision,5:P0} {r.Recall,6:P0} {r.ExactCases,5} {r.ConfidentWrong,9} {r.P50,6} {r.P95,6} {r.AvgIn,6:F0} {r.CachedShare,6:P0} {r.AvgOut,6:F0} {r.Errors,3}");
}

var report = Path.Combine(Path.GetTempPath(), "kupro-meeting-agent-spike.json");
File.WriteAllText(report, JsonSerializer.Serialize(results.OrderByDescending(r => r.F1).Select(r => new
{
    r.Model, r.Precision, r.Recall, r.ConfidentWrong, r.P50, r.P95, r.AvgIn, r.CachedShare, r.AvgOut, r.Errors, Cases = r.Rows,
}), new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine($"per-case detail: {report}");
Console.WriteLine("For the pricing skill: extraction.inputTokens = tokIn, cachedShare = cached, outputTokens = tokOut.");

static string? UserSecret(string id, string name)
{
    var root = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "UserSecrets")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".microsoft", "usersecrets");
    var file = Path.Combine(root, id, "secrets.json");
    return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file))?[name]?.GetValue<string>() : null;
}

static CaseResult Score(Case c, IReadOnlyList<ExtractedAction> actions, long ms, Usage call, string? error, Dictionary<string, string> titles)
{
    var unmatched = c.Expected.ToList();
    var got = new List<ActionResult>();
    foreach (var a in actions)
    {
        var hit = unmatched.FirstOrDefault(e => e.Op == a.Operation && IssueMatches(e.Issue, a.IssueReference, titles) && ValueMatches(e, a.Value));
        if (hit is not null)
        {
            unmatched.Remove(hit);
        }

        got.Add(new ActionResult(a.Operation.ToString(), a.IssueReference, a.Value, a.Confidence, hit is not null));
    }

    return new CaseResult(c.Id, c.Utterance, c.Expected.Length, got, unmatched.Count, ms, call.In, call.Cached, call.Out, error,
        error is null ? null : call.Raw);
}

// Production resolves a non-key reference by search (MeetingService.ResolveIssueAsync); an exact title or a
// fragment of it stands in for that here.
static bool IssueMatches(string expected, string actual, Dictionary<string, string> titles)
{
    var a = actual.Trim();
    if (expected.Length == 0 || string.Equals(expected, a, StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return a.Length >= 4 && titles[expected].Contains(a, StringComparison.OrdinalIgnoreCase)
        && !titles.Any(t => t.Key != expected && t.Value.Contains(a, StringComparison.OrdinalIgnoreCase));
}

static bool ValueMatches(Exp e, string? value)
{
    var v = (value ?? "").Trim();
    if (e.Values.Contains("*"))
    {
        return v.Length > 0;
    }

    return e.Op switch
    {
        ProposalOperation.Estimate => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && e.Values.Any(x => Math.Abs(double.Parse(x, CultureInfo.InvariantCulture) - n) < 0.01),
        ProposalOperation.Assignee => e.Values.Any(x => x == "none"
            ? v.Equals("none", StringComparison.OrdinalIgnoreCase) || v.Length == 0
            : v.Contains(x, StringComparison.OrdinalIgnoreCase)),
        ProposalOperation.Sprint => e.Values.Any(x => v.Contains(x, StringComparison.OrdinalIgnoreCase)),
        _ => e.Values.Any(x => v.Equals(x, StringComparison.OrdinalIgnoreCase)),
    };
}

record Exp(ProposalOperation Op, string Issue, string[] Values);
record Case(string Id, string Language, MeetingType Type, string[] Recent, string Utterance, Exp[] Expected);
record ActionResult(string Op, string Issue, string? Value, double Confidence, bool Correct);
record CaseResult(string Id, string Utterance, int Expected, List<ActionResult> Got, int Missed, long Ms, int TokensIn, int TokensCached,
    int TokensOut, string? Error, string? Raw);
record Usage(int In, int Cached, int Out, string? FinishReason, string? Raw);

record ModelResult(string Model, List<CaseResult> Rows)
{
    private IEnumerable<ActionResult> All => Rows.SelectMany(r => r.Got);
    private IEnumerable<CaseResult> Ok => Rows.Where(r => r.Error is null);
    public double Precision => All.Any() ? (double)All.Count(a => a.Correct) / All.Count() : 0;
    public double Recall => (double)All.Count(a => a.Correct) / Math.Max(1, Rows.Sum(r => r.Expected));
    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);
    public string ExactCases => $"{Rows.Count(r => r.Error is null && r.Missed == 0 && r.Got.All(a => a.Correct))}/{Rows.Count}";
    public int ConfidentWrong => All.Count(a => !a.Correct && a.Confidence >= MeetingService.AutoApplyConfidence);
    public long P50 => Pct(0.5);
    public long P95 => Pct(0.95);
    public double AvgIn => Ok.Select(r => (double)r.TokensIn).DefaultIfEmpty(0).Average();
    public double AvgOut => Ok.Select(r => (double)r.TokensOut).DefaultIfEmpty(0).Average();
    public double CachedShare => Ok.Sum(r => r.TokensIn) is var total and > 0 ? (double)Ok.Sum(r => r.TokensCached) / total : 0;
    public int Errors => Rows.Count(r => r.Error is not null);

    private long Pct(double p)
    {
        var ms = Rows.Select(r => r.Ms).Order().ToList();
        return ms[(int)Math.Min(ms.Count - 1, Math.Ceiling(p * ms.Count) - 1)];
    }
}

/// <summary>Records each completion's token usage, so the production extractor runs unchanged.</summary>
sealed class UsageHandler : DelegatingHandler
{
    private Usage _last = new(0, 0, 0, null, null);

    public Usage Take() => Interlocked.Exchange(ref _last, new Usage(0, 0, 0, null, null));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode}: {body[..Math.Min(300, body.Length)]}");
        }

        var json = JsonNode.Parse(body);
        var usage = json?["usage"];
        var content = json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        _last = new Usage(
            usage?["prompt_tokens"]?.GetValue<int>() ?? 0,
            usage?["prompt_tokens_details"]?["cached_tokens"]?.GetValue<int>() ?? 0,
            usage?["completion_tokens"]?.GetValue<int>() ?? 0,
            json?["choices"]?[0]?["finish_reason"]?.GetValue<string>(),
            content[..Math.Min(400, content.Length)]);
        response.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        return response;
    }
}
