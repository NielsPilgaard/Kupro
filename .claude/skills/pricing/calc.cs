#:property TreatWarningsAsErrors=false
#:property PublishAot=false
#:property NoWarn=$(NoWarn);CA1303;CA1305;CA1307;CA1310;CA1852;IL2026;IL3050
// Pricing calculator for the pricing skill. Reads pricing.json next to this file and prints every derived number the
// docs quote, so they come from one formula instead of hand arithmetic.
//
//   dotnet run .claude/skills/pricing/calc.cs                                   # current numbers
//   dotnet run .claude/skills/pricing/calc.cs -- trial.meetingHours=3           # what-if, nothing saved
//   dotnet run .claude/skills/pricing/calc.cs -- trial.meetingHours=3 --write   # save into pricing.json
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

var inv = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = inv;
var file = Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? ".", "pricing.json");
var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
var changed = new List<string>();

foreach (var arg in args.Where(a => a.Contains('=', StringComparison.Ordinal)))
{
    var (path, raw) = (arg[..arg.IndexOf('=', StringComparison.Ordinal)], arg[(arg.IndexOf('=', StringComparison.Ordinal) + 1)..]);
    var parts = path.Split('.');
    var parent = parts[..^1].Aggregate((JsonNode)root, (n, p) => n[p] ?? throw new ArgumentException($"Unknown key {path}"));
    if (parent[parts[^1]] is null)
    {
        throw new ArgumentException($"Unknown key {path}");
    }

    var before = parent[parts[^1]]!.ToJsonString();
    parent[parts[^1]] = double.TryParse(raw, NumberStyles.Float, inv, out var n) ? JsonValue.Create(n) : JsonValue.Create(raw);
    changed.Add($"{path}: {before} -> {parent[parts[^1]]!.ToJsonString()}");
}

double D(string path) => path.Split('.').Aggregate((JsonNode?)root, (n, p) => n?[p])?.GetValue<double>()
    ?? throw new ArgumentException($"Missing {path} in pricing.json");
string Eur(double v) => "€" + v.ToString(v == Math.Floor(v) ? "0" : "0.00", inv);
string About(double v) => "≈ €" + (Math.Ceiling(v * 10) / 10).ToString("0.00", inv);

// Meeting agent: one microphone for one hour.
var x = "meetingAgent.extraction.";
var whisperHour = D("meetingAgent.transcription.eurPerAudioMinute") * 60;
var input = D(x + "inputTokens");
var cached = D(x + "cachedShare");
var perCall = (input * (1 - cached) * D(x + "eurPerMInput") + input * cached * D(x + "eurPerMCachedInput")
    + D(x + "outputTokens") * D(x + "eurPerMOutput")) / 1_000_000;
var llmHour = perCall * D("meetingAgent.callsPerHour");
var hour = whisperHour + llmHour;

var monthly = D("seat.monthlyEur");
var yearly = D("seat.yearlyEur");
var yearlyMonth = yearly / 12;
var seatHours = D("allowance.hoursPerSeat");
var maxShare = D("allowance.maxAiShareOfSeat");
var trialHours = D("trial.meetingHours");
var standupHours = D("examples.standupMinutes") * D("examples.workdaysPerMonth") / 60;
var team = D("examples.standupTeamSize");

if (changed.Count > 0)
{
    Console.WriteLine("## What-if");
    changed.ForEach(c => Console.WriteLine($"- {c}"));
    Console.WriteLine();
}

Console.WriteLine($"""
    ## Meeting agent, one microphone for one hour
    | Part | Per meeting hour |
    |---|---|
    | Transcription ({root["meetingAgent"]!["transcription"]!["model"]}) | {Eur(whisperHour)} |
    | Proposals ({root["meetingAgent"]!["extraction"]!["model"]}, {D("meetingAgent.callsPerHour"):0} calls at €{perCall:0.00000} each) | {Eur(llmHour)} |
    | **Total** (MeetingAgent:Budget euro per hour) | **{Eur(hour)}** |

    ## Seats
    - Monthly {Eur(monthly)} / {D("seat.monthlyDkk")} kr (kr/€ {D("seat.monthlyDkk") / monthly:0.00}); yearly {Eur(yearly)} / {D("seat.yearlyDkk")} kr = {Eur(yearlyMonth)} a month, {12 - yearly / monthly:0.#} months free (kr/€ {D("seat.yearlyDkk") / yearly:0.00}).
    - Allowance {seatHours:0.#} h per seat: at most {Eur(seatHours * hour)} a seat a month, {seatHours * hour / monthly:P0} of monthly, {seatHours * hour / yearlyMonth:P0} of yearly.
    - Guard: the allowance may use at most {maxShare:P0} of a yearly seat ({(seatHours * hour / yearlyMonth <= maxShare ? "OK" : "OVER")}); any discount on yearly above {Math.Max(0, 1 - seatHours * hour / (maxShare * yearlyMonth)):P0} breaks it.

    ## Trial
    - {D("trial.days"):0} days, {trialHours:0.#} meeting hours in total: at most {Eur(trialHours * hour)} per trial, {trialHours * 60 / D("examples.standupMinutes"):0} standups of {D("examples.standupMinutes"):0} minutes.

    ## Examples the docs quote
    - {team:0}-person team, {D("examples.standupMinutes"):0}-min standup every workday: {standupHours:0.#} h/month of {team * seatHours:0.#} pooled hours ({(standupHours <= team * seatHours ? "fits" : "DOES NOT FIT")}), costs {About(standupHours * hour)}/mo.
    - Docs wording: "{seatHours:0.#} hours per seat a month costs at most about {About(seatHours * hour)[2..]} per {Eur(monthly)} seat"; trial "at most about {About(trialHours * hour)[2..]} per trial".
    """);

if (args.Contains("--write"))
{
    root["updated"] = DateTime.Now.ToString("yyyy-MM-dd", inv);
    File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    Console.WriteLine($"Saved {file}");
}
