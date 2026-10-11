using System.CommandLine;
using System.Globalization;
using Flowstate.Cli.Api.Models;
using Microsoft.Kiota.Abstractions;

namespace Flowstate.Cli.Commands;

/// <summary>plan, capacity get|set, milestone list|create|update|delete.</summary>
internal static class PlanCommands
{
    public static IEnumerable<Command> Build()
    {
        yield return Plan();
        yield return Capacity();
        yield return Milestones();
    }

    private static Command Plan()
    {
        var from = new Option<string?>("--from") { Description = "First month, YYYY-MM (default: this month)." };
        var to = new Option<string?>("--to") { Description = "Last month, YYYY-MM (default: December next year)." };
        var project = new Option<string[]>("--project", "-p") { Description = "Project key; repeatable.", AllowMultipleArgumentsPerToken = true };
        return new ApiCommand("plan", "Demand vs supply per month: where you're short of people.", "getPlan") { from, to, project }
            .Handle(async (c, ct) =>
            {
                var plan = await (await c.InWorkspaceAsync(ct)).Plan.GetAsync(r =>
                {
                    r.QueryParameters.From = c.Get(from) is { } f ? ParseMonth(f) : null;
                    r.QueryParameters.To = c.Get(to) is { } t ? ParseMonth(t) : null;
                    r.QueryParameters.Project = c.Get(project) is { Length: > 0 } p ? p.Select(k => k.ToUpperInvariant()).ToArray() : null;
                }, ct);
                if (c.Out.Json)
                {
                    await c.Out.ItemAsync(plan!);
                    return;
                }

                await c.Out.ListAsync(plan!.Totals ?? [], ["MONTH", "NEEDS", "HAS", "GAP"], m =>
                    [m.Month?.ToString()?[..7], Format(m.DemandPeople), Format(m.SupplyPeople),
                        m.GapPeople > 0 ? $"short {Format(m.GapPeople)}" : m.GapPeople < 0 ? $"spare {Format(-m.GapPeople)}" : ""]);
                foreach (var p in plan.Projects ?? [])
                {
                    var flags = new List<string>();
                    if (p.Health?.NoDatedWork == true) flags.Add("no dated work in the horizon");
                    if (p.Health?.CardsWithoutDates > 0) flags.Add($"{p.Health.CardsWithoutDates} cards without dates");
                    if (p.Health?.CardsWithoutEffort > 0) flags.Add($"{p.Health.CardsWithoutEffort} cards without effort");
                    if (flags.Count > 0) await c.Out.MessageAsync($"! {p.Key}: {string.Join(", ", flags)}");
                }

                foreach (var d in (plan.Dependencies ?? []).Where(d => d.Violated == true))
                {
                    await c.Out.MessageAsync($"! {d.Blocked} starts before its blocker {d.Blocker} is due");
                }
            });
    }

    private static Command Capacity()
    {
        var capacity = new Command("capacity", "People per project from a month onward.");
        var key = new Argument<string>("project") { Description = "Project key." };
        capacity.Subcommands.Add(new ApiCommand("get", "Show a project's headcount steps.", "getProjectCapacity") { key }.Handle(async (c, ct) =>
        {
            var steps = await (await c.InWorkspaceAsync(ct)).Projects[c.Get(key)!.ToUpperInvariant()].Capacity.GetAsync(cancellationToken: ct) ?? [];
            await c.Out.ListAsync(steps, ["FROM", "PEOPLE"], s => [s.FromMonth?.ToString()?[..7], Format(s.People)]);
        }));

        var steps = new Argument<string[]>("steps") { Description = "Steps like 3@2027-01 5@2027-07 (people@month). None clears." };
        steps.Arity = ArgumentArity.ZeroOrMore;
        capacity.Subcommands.Add(new ApiCommand("set", "Replace a project's headcount steps, e.g. `capacity set FLOW 3@2027-01 5@2027-07`.",
            "setProjectCapacity") { key, steps }.Handle(async (c, ct) =>
        {
            var entries = (c.Get(steps) ?? []).Select(ParseStep).ToList();
            var saved = await (await c.InWorkspaceAsync(ct)).Projects[c.Get(key)!.ToUpperInvariant()].Capacity.PutAsync(
                new SetCapacityRequest { Entries = entries }, cancellationToken: ct) ?? [];
            await c.Out.ListAsync(saved, ["FROM", "PEOPLE"], s => [s.FromMonth?.ToString()?[..7], Format(s.People)]);
        }));
        return capacity;
    }

    private static Command Milestones()
    {
        var milestone = new Command("milestone", "Dated markers on the delivery plan.");
        milestone.Subcommands.Add(new ApiCommand("list", "List milestones.", "listMilestones").Handle(async (c, ct) =>
        {
            var list = await (await c.InWorkspaceAsync(ct)).Milestones.GetAsync(cancellationToken: ct) ?? [];
            await c.Out.ListAsync(list, ["ID", "DATE", "PROJECT", "NAME"], m => [m.Id?.ToString(), m.Date?.ToString(), m.ProjectKey ?? "all", m.Name]);
        }));

        var name = new Option<string>("--name") { Description = "Name.", Required = true };
        var date = new Option<string>("--date") { Description = "Date, YYYY-MM-DD.", Required = true };
        var project = new Option<string?>("--project", "-p") { Description = "Project key; workspace-wide when omitted." };
        milestone.Subcommands.Add(new ApiCommand("create", "Add a milestone.", "createMilestone") { name, date, project }.Handle(async (c, ct) =>
        {
            var m = await (await c.InWorkspaceAsync(ct)).Milestones.PostAsync(new CreateMilestoneRequest
            {
                Name = c.Get(name),
                Date = ParseDate(c.Get(date)!),
                Project = c.Get(project)?.ToUpperInvariant(),
            }, cancellationToken: ct);
            await c.Out.DoneAsync(m!, $"Added {m!.Name} on {m.Date} ({m.Id}).");
        }));

        var id = new Argument<Guid>("id") { Description = "Milestone id." };
        var newName = new Option<string?>("--name") { Description = "New name." };
        var newDate = new Option<string?>("--date") { Description = "New date, YYYY-MM-DD." };
        milestone.Subcommands.Add(new ApiCommand("update", "Rename or move a milestone.", "updateMilestone") { id, newName, newDate }.Handle(async (c, ct) =>
        {
            var m = await (await c.InWorkspaceAsync(ct)).Milestones[c.Get(id)].PatchAsync(new UpdateMilestoneRequest
            {
                Name = c.Get(newName),
                Date = c.Get(newDate) is { } d ? ParseDate(d) : null,
            }, cancellationToken: ct);
            await c.Out.DoneAsync(m!, $"Updated {m!.Name}.");
        }));

        milestone.Subcommands.Add(new ApiCommand("delete", "Delete a milestone.", "deleteMilestone") { id }.Handle(async (c, ct) =>
        {
            await (await c.InWorkspaceAsync(ct)).Milestones[c.Get(id)].DeleteAsync(cancellationToken: ct);
            await c.Out.MessageAsync("Milestone deleted.");
        }));
        return milestone;
    }

    private static CapacityEntry ParseStep(string step)
    {
        var parts = step.Split('@');
        return parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var people)
            ? new CapacityEntry { People = people, FromMonth = ParseMonth(parts[1]) }
            : throw new CliException($"'{step}' isn't people@YYYY-MM, e.g. 3@2027-01.");
    }

    private static Date ParseMonth(string value) =>
        DateOnly.TryParseExact(value + (value.Length == 7 ? "-01" : ""), "yyyy-MM-dd", out var d)
            ? new Date(d.Year, d.Month, 1)
            : throw new CliException($"Month '{value}' isn't YYYY-MM.");

    private static Date ParseDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", out var d)
            ? new Date(d.Year, d.Month, d.Day)
            : throw new CliException($"Date '{value}' isn't YYYY-MM-DD.");

    private static string Format(double? value) => (value ?? 0).ToString("0.#", CultureInfo.InvariantCulture);
}
