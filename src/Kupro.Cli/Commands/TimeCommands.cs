using System.CommandLine;
using System.Globalization;
using System.Text.RegularExpressions;
using Kupro.Cli.Api.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Kupro.Cli.Commands;

/// <summary>
/// time log|week|edit|delete|issue|history, and for Owners time list|export (task 034, ADR-021). Durations read as
/// <c>1h30m</c>, <c>90m</c>, <c>1.5h</c> or <c>1:30</c>.
/// </summary>
internal static partial class TimeCommands
{
    public static IEnumerable<Command> Build()
    {
        var time = new Command("time", "Time registration: log time on issues, your week, and the owners' export.");
        time.Subcommands.Add(Log());
        time.Subcommands.Add(Week());
        time.Subcommands.Add(Edit());
        time.Subcommands.Add(Delete());
        time.Subcommands.Add(ForIssue());
        time.Subcommands.Add(History());
        time.Subcommands.Add(List());
        time.Subcommands.Add(Export());
        yield return time;
    }

    private static Command Log()
    {
        var duration = new Argument<string>("duration") { Description = "Time spent: 1h30m, 90m, 1.5h or 1:30." };
        var key = new Argument<string>("key") { Description = "Issue key, e.g. KUP-12." };
        var date = DateOption("--date", "Day it was spent, YYYY-MM-DD (default today).");
        var note = new Option<string?>("--note", "-n") { Description = "What it went on." };
        var done = new Option<bool>("--done") { Description = "Also mark the issue done, in the same save." };
        var canceled = new Option<bool>("--canceled") { Description = "Also mark the issue canceled, in the same save." };
        return new ApiCommand("log", "Log time on an issue.", "logTime") { duration, key, date, note, done, canceled }.Handle(async (c, ct) =>
        {
            if (c.Get(done) && c.Get(canceled))
            {
                throw new CliException("Pick --done or --canceled, not both.");
            }

            var entry = await (await c.InWorkspaceAsync(ct)).Time.PostAsync(new LogTimeRequest
            {
                Issue = c.Get(key),
                Minutes = ParseDuration(c.Get(duration)!),
                Date = ToDate(c.Get(date)),
                Note = c.Get(note),
                State = c.Get(done) ? IssueState.Done : c.Get(canceled) ? IssueState.Canceled : null,
            }, cancellationToken: ct);
            var closed = c.Get(done) ? " and marked it done" : c.Get(canceled) ? " and marked it canceled" : "";
            await c.Out.DoneAsync(entry!, $"Logged {Format(entry!.Minutes)} on {entry.Issue!.Key} for {entry.Date}{closed} ({entry.Id}).");
        });
    }

    private static Command Week()
    {
        var date = DateOption("--date", "Any day in the week (default today).");
        var from = DateOption("--from", "First day of a custom range, instead of a week.");
        var to = DateOption("--to", "Last day of a custom range (at most 93 days).");
        return new ApiCommand("week", "Your time for a week, Monday to Sunday: entries and day totals.", "listMyTime") { date, from, to }.Handle(async (c, ct) =>
        {
            // Without from, the server picks the week of `to` (default its today), so the week follows its calendar.
            var last = c.Get(to) ?? (c.Get(from) is null ? c.Get(date) : null);
            var mine = await (await c.InWorkspaceAsync(ct)).Time.Mine.GetAsync(
                r => (r.QueryParameters.From, r.QueryParameters.To) = (ToDate(c.Get(from)), ToDate(last)), ct);
            if (c.Out.Json)
            {
                await c.Out.ItemAsync(mine!);
                return;
            }

            await c.Out.ListAsync(mine!.Entries ?? [], ["DATE", "TIME", "ISSUE", "ID", "NOTE"], e =>
                [e.Date.ToString(), Format(e.Minutes), e.Issue?.Key + (e.Locked == true ? " (locked)" : ""), e.Id.ToString(), e.Note ?? e.Issue?.Title]);
            await c.Out.MessageAsync("");
            await c.Out.MessageAsync(string.Join("  ", (mine.Days ?? []).Select(d =>
                $"{DayName(d.Date)} {Format(d.Minutes)}")) + $"  = {Format(mine.TotalMinutes)}");
        });
    }

    private static Command Edit()
    {
        var id = new Argument<Guid>("id") { Description = "Entry id (see `time week`)." };
        var duration = new Option<string?>("--duration", "-d") { Description = "New duration: 1h30m, 90m, 1.5h or 1:30." };
        var date = DateOption("--date", "Move it to another day, YYYY-MM-DD.");
        var note = new Option<string?>("--note", "-n") { Description = "New note; \"\" clears it." };
        return new ApiCommand("edit", "Change one of your entries.", "updateTimeEntry") { id, duration, date, note }.Handle(async (c, ct) =>
        {
            var entry = await (await c.InWorkspaceAsync(ct)).Time[c.Get(id)].PatchAsync(new UpdateTimeEntryRequest
            {
                Minutes = c.Get(duration) is { } d ? ParseDuration(d) : null,
                Date = ToDate(c.Get(date)),
                Note = c.Get(note),
            }, cancellationToken: ct);
            await c.Out.DoneAsync(entry!, $"{entry!.Issue!.Key} on {entry.Date}: {Format(entry.Minutes)}.");
        });
    }

    private static Command Delete()
    {
        var id = new Argument<Guid>("id") { Description = "Entry id." };
        return new ApiCommand("delete", "Delete one of your entries (its history is kept).", "deleteTimeEntry") { id }.Handle(async (c, ct) =>
        {
            await (await c.InWorkspaceAsync(ct)).Time[c.Get(id)].DeleteAsync(cancellationToken: ct);
            await c.Out.MessageAsync($"Deleted time entry {c.Get(id)}.");
        });
    }

    private static Command ForIssue()
    {
        var key = new Argument<string>("key") { Description = "Issue key." };
        return new ApiCommand("issue", "Time on an issue: yours, and for owners everyone's.", "getIssueTime") { key }.Handle(async (c, ct) =>
        {
            var t = await (await c.InWorkspaceAsync(ct)).Issues[c.Get(key)!].Time.GetAsync(cancellationToken: ct);
            await c.Out.ItemAsync(t!, ("Issue", t!.IssueKey), ("Yours", Format(t.MyMinutes)), ("Total", t.TotalMinutes is { } total ? Format(total) : null),
                ("People", t.People is { Count: > 0 } people ? string.Join(", ", people.Select(p => $"{p.Member?.Name} {Format(p.Minutes)}")) : null),
                ("Locked", t.Locked == true ? "yes (done or canceled)" : "no"));
        });
    }

    private static Command History()
    {
        var member = new Option<Guid?>("--member") { Description = "Member id (owners; default you)." };
        var before = new Option<Guid?>("--before") { Description = "Older changes: the id of the oldest change you have." };
        var limit = new Option<int?>("--limit") { Description = "Changes per page, 1-200 (default 100)." };
        return new ApiCommand("history", "Every create, edit and delete of time entries, newest first.", "listTimeChanges") { member, before, limit }.Handle(async (c, ct) =>
        {
            var changes = await (await c.InWorkspaceAsync(ct)).Time.Changes.GetAsync(
                r => (r.QueryParameters.Member, r.QueryParameters.Before, r.QueryParameters.Limit) = (c.Get(member), c.Get(before), c.Get(limit)), ct) ?? [];
            await c.Out.ListAsync(changes, ["WHEN", "WHO", "VIA", "WHAT", "ID"], ch =>
                [ch.At?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), ch.ChangedBy?.Name,
                    ch.Via?.ToString()?.ToLowerInvariant(), Describe(ch), ch.Id.ToString()]);
        });
    }

    private static Command List()
    {
        var (from, to, member) = RangeOptions();
        return new ApiCommand("list", "Everyone's entries for up to 93 days, or one member's (owners).", "listTimeEntries") { from, to, member }.Handle(async (c, ct) =>
        {
            var list = await (await c.InWorkspaceAsync(ct)).Time.Entries.GetAsync(
                r => (r.QueryParameters.From, r.QueryParameters.To, r.QueryParameters.Member) = (ToDate(c.Get(from)), ToDate(c.Get(to)), c.Get(member)), ct);
            if (c.Out.Json)
            {
                await c.Out.ItemAsync(list!);
                return;
            }

            await c.Out.ListAsync(list!.Items ?? [], ["DATE", "WHO", "TIME", "ISSUE", "NOTE"], e =>
                [e.Date.ToString(), e.Member?.Name, Format(e.Minutes), e.Issue?.Key, e.Note ?? e.Issue?.Title]);
            await c.Out.MessageAsync($"Total {Format(list.TotalMinutes)}");
        });
    }

    private static Command Export()
    {
        var (from, to, member) = RangeOptions();
        var format = Cli.EnumOption<TimeExportFormat>("--format", "File format (default csv)");
        var output = new Option<FileInfo?>("--output", "-o") { Description = "Write to this file (default stdout)." };
        return new ApiCommand("export", "Export everyone's time, or one member's, for up to 366 days as CSV or JSON (owners).", "exportTime")
            { from, to, member, format, output }.Handle(async (c, ct) =>
        {
            var request = (await c.InWorkspaceAsync(ct)).Time.Export.ToGetRequestInformation(r =>
                (r.QueryParameters.From, r.QueryParameters.To, r.QueryParameters.Member, r.QueryParameters.Format) =
                (ToDate(c.Get(from)), ToDate(c.Get(to)), c.Get(member), c.Get(format) ?? TimeExportFormat.Csv));

            // The generated client reads the body as JSON rows; the file is wanted as sent, CSV included.
            var errors = new Dictionary<string, ParsableFactory<IParsable>> { ["XXX"] = ProblemDetails.CreateFromDiscriminatorValue };
            await using var body = await c.Adapter.SendPrimitiveAsync<Stream>(request, errors, ct)
                ?? throw new CliException("The export was empty.");
            if (c.Get(output) is { } file)
            {
                await using var sink = file.Create();
                await body.CopyToAsync(sink, ct);
                await c.Out.MessageAsync($"Saved to {file.FullName}.");
                return;
            }

            using var reader = new StreamReader(body);
            var buffer = new char[16 * 1024];
            int read;
            while ((read = await reader.ReadAsync(buffer, ct)) > 0)
            {
                await c.Out.Writer.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        });
    }

    private static (Option<DateOnly?> From, Option<DateOnly?> To, Option<Guid?> Member) RangeOptions()
    {
        var from = DateOption("--from", "First day, YYYY-MM-DD.");
        var to = DateOption("--to", "Last day, YYYY-MM-DD.");
        from.Required = to.Required = true;
        return (from, to, new Option<Guid?>("--member") { Description = "Member id; everyone when omitted." });
    }

    private static Option<DateOnly?> DateOption(string name, string description) => new(name)
    {
        Description = description,
        CustomParser = result =>
        {
            var value = result.Tokens.Single().Value;
            if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                return d;
            }

            result.AddError($"Date '{value}' isn't YYYY-MM-DD.");
            return null;
        },
    };

    private static Date? ToDate(DateOnly? d) => d is { } v ? new Date(v.Year, v.Month, v.Day) : null;

    /// <summary>Minutes from <c>1h30m</c>, <c>90m</c>, <c>1.5h</c>, <c>2h</c> or <c>1:30</c>.</summary>
    internal static int ParseDuration(string text)
    {
        var value = text.Trim().ToLowerInvariant().Replace(',', '.');
        int? minutes = null;
        if (Clock().Match(value) is { Success: true } clock)
        {
            minutes = (int.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture) * 60) + int.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        else if (Units().Match(value) is { Success: true } units && (units.Groups[1].Success || units.Groups[2].Success))
        {
            var hours = units.Groups[1].Success ? decimal.Parse(units.Groups[1].Value, CultureInfo.InvariantCulture) : 0m;
            var mins = units.Groups[2].Success ? int.Parse(units.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            minutes = (int)Math.Round((hours * 60) + mins, MidpointRounding.AwayFromZero);
        }

        return minutes is > 0 and <= 1440 ? minutes.Value
            : throw new CliException($"Duration '{text}' isn't 1 minute to 24 hours, written like 1h30m, 90m, 1.5h or 1:30.");
    }

    internal static string Format(int? minutes) => minutes is not { } m ? "-"
        : m % 60 == 0 ? $"{m / 60}h"
        : m < 60 ? $"{m}m"
        : $"{m / 60}h{m % 60:00}m";

    private static string DayName(Date? date) => date is { } d
        ? new DateOnly(d.Year, d.Month, d.Day).ToString("ddd dd", CultureInfo.InvariantCulture)
        : "";

    private static string Describe(TimeEntryChangeResponse ch) => ch.Kind switch
    {
        TimeChangeKind.Created => $"logged {Format(ch.NewMinutes)} on {ch.Issue} for {ch.NewDate}",
        TimeChangeKind.Deleted => $"deleted {Format(ch.OldMinutes)} on {ch.Issue} for {ch.OldDate}",
        _ => $"{ch.Issue}: {Format(ch.OldMinutes)} {ch.OldDate} -> {Format(ch.NewMinutes)} {ch.NewDate}"
             + (ch.OldNote != ch.NewNote ? ", note changed" : ""),
    };

    [GeneratedRegex(@"^(\d{1,2}):([0-5]\d)$")]
    private static partial Regex Clock();

    [GeneratedRegex(@"^(?:(\d+(?:\.\d+)?)h)?\s*(?:(\d+)m(?:in)?)?$")]
    private static partial Regex Units();
}
