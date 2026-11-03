using System.CommandLine;
using System.Globalization;
using Kupro.Cli.Api.Models;
using Microsoft.Kiota.Abstractions;

namespace Kupro.Cli.Commands;

/// <summary>sprint list|get|create|update|start|close|review|burndown.</summary>
internal static class SprintCommands
{
    public static IEnumerable<Command> Build()
    {
        var sprint = new Command("sprint", "Sprints per project. Add issues with `issue update KEY --sprint active`.");
        sprint.Subcommands.Add(List());
        sprint.Subcommands.Add(Get());
        sprint.Subcommands.Add(Create());
        sprint.Subcommands.Add(Update());
        sprint.Subcommands.Add(Start());
        sprint.Subcommands.Add(Close());
        sprint.Subcommands.Add(Review());
        sprint.Subcommands.Add(Burndown());
        yield return sprint;
    }

    private static Command List()
    {
        var project = new Option<string?>("--project", "-p") { Description = "Project key." };
        var state = Cli.EnumListOption<SprintState>("--state", "State");
        return new ApiCommand("list", "List sprints.", "listSprints") { project, state }.Handle(async (c, ct) =>
        {
            var list = await (await c.InWorkspaceAsync(ct)).Sprints.GetAsync(r =>
            {
                r.QueryParameters.Project = c.Get(project)?.ToUpperInvariant();
                r.QueryParameters.State = c.Get(state) is { Length: > 0 } s ? s : null;
            }, ct) ?? [];
            await c.Out.ListAsync(list, ["ID", "PROJECT", "STATE", "DATES", "HOURS", "NAME"], s =>
                [s.Id?.ToString(), s.ProjectKey, Cli.Dashed(s.State.ToString()!), $"{s.StartDate}..{s.EndDate}",
                    $"{s.DoneEffort}/{s.Effort}", s.Name]);
        });
    }

    private static Command Get()
    {
        var id = IdArgument();
        return new ApiCommand("get", "Show a sprint.", "getSprint") { id }.Handle(async (c, ct) =>
        {
            var s = await (await c.InWorkspaceAsync(ct)).Sprints[c.Get(id)].GetAsync(cancellationToken: ct);
            await c.Out.ItemAsync(s!, ("Name", s!.Name), ("Project", s.ProjectKey), ("State", Cli.Dashed(s.State.ToString()!)),
                ("Dates", $"{s.StartDate} .. {s.EndDate}"), ("Issues", s.IssueCount?.ToString()),
                ("Hours", $"{s.DoneEffort} done of {s.Effort}"), ("Goal", s.Goal));
        });
    }

    private static Command Create()
    {
        var project = new Option<string>("--project", "-p") { Description = "Project key.", Required = true };
        var name = new Option<string>("--name") { Description = "Sprint name.", Required = true };
        var start = new Option<string>("--start") { Description = "First day, YYYY-MM-DD.", Required = true };
        var end = new Option<string>("--end") { Description = "Last day, YYYY-MM-DD.", Required = true };
        var goal = new Option<string?>("--goal") { Description = "Sprint goal." };
        return new ApiCommand("create", "Plan a sprint.", "createSprint") { project, name, start, end, goal }.Handle(async (c, ct) =>
        {
            var s = await (await c.InWorkspaceAsync(ct)).Sprints.PostAsync(new CreateSprintRequest
            {
                Project = c.Get(project)!.ToUpperInvariant(),
                Name = c.Get(name),
                StartDate = ParseDate(c.Get(start)!),
                EndDate = ParseDate(c.Get(end)!),
                Goal = c.Get(goal),
            }, cancellationToken: ct);
            await c.Out.DoneAsync(s!, $"Planned {s!.Name} ({s.Id}).");
        });
    }

    private static Command Update()
    {
        var id = IdArgument();
        var name = new Option<string?>("--name") { Description = "New name." };
        var start = new Option<string?>("--start") { Description = "New first day, YYYY-MM-DD." };
        var end = new Option<string?>("--end") { Description = "New last day, YYYY-MM-DD." };
        var goal = new Option<string?>("--goal") { Description = "New goal." };
        return new ApiCommand("update", "Rename a sprint or change its dates or goal.", "updateSprint") { id, name, start, end, goal }
            .Handle(async (c, ct) =>
            {
                var s = await (await c.InWorkspaceAsync(ct)).Sprints[c.Get(id)].PatchAsync(new UpdateSprintRequest
                {
                    Name = c.Get(name),
                    StartDate = c.Get(start) is { } st ? ParseDate(st) : null,
                    EndDate = c.Get(end) is { } en ? ParseDate(en) : null,
                    Goal = c.Get(goal),
                }, cancellationToken: ct);
                await c.Out.DoneAsync(s!, $"Updated {s!.Name}.");
            });
    }

    private static Command Start()
    {
        var id = IdArgument();
        return new ApiCommand("start", "Start a planned sprint.", "startSprint") { id }.Handle(async (c, ct) =>
        {
            var s = await (await c.InWorkspaceAsync(ct)).Sprints[c.Get(id)].Start.PostAsync(cancellationToken: ct);
            await c.Out.DoneAsync(s!, $"Started {s!.Name}.");
        });
    }

    private static Command Close()
    {
        var sprint = SprintArgument();
        var project = ActiveProjectOption();
        var to = new Option<string?>("--to") { Description = "Where unlisted issues go: next (default), backlog, or a sprint id. A sprint id is also the target for splits." };
        var split = new Option<string[]>("--split") { Description = "KEY=HOURS: done here with the rest, a new issue takes HOURS to the target. Repeat for several.", AllowMultipleArgumentsPerToken = true };
        var toBacklog = new Option<string[]>("--backlog") { Description = "Issue keys to move to the backlog.", AllowMultipleArgumentsPerToken = true };
        var cancel = new Option<string[]>("--cancel") { Description = "Issue keys to cancel.", AllowMultipleArgumentsPerToken = true };
        var done = new Option<string[]>("--done") { Description = "Issue keys to mark done.", AllowMultipleArgumentsPerToken = true };
        var startNext = new Option<bool>("--start-next") { Description = "Start the target sprint right after closing." };
        return new ApiCommand("close", "Close the active sprint with an outcome per unfinished issue; the rest carry to the next sprint.", "closeSprint")
            { sprint, project, to, split, toBacklog, cancel, done, startNext }
            .Handle(async (c, ct) =>
            {
                var id = await ResolveAsync(c, c.Get(sprint)!, c.Get(project), ct);
                var items = (c.Get(split) ?? []).Select(ParseSplit).ToList();
                items.AddRange(Items(c.Get(toBacklog), CloseOutcome.Backlog));
                items.AddRange(Items(c.Get(cancel), CloseOutcome.Cancel));
                items.AddRange(Items(c.Get(done), CloseOutcome.Done));
                var destination = c.Get(to)?.ToLowerInvariant();
                Guid? target = null;
                if (destination is not (null or "next" or "backlog"))
                {
                    target = Guid.TryParse(destination, out var parsed)
                        ? parsed
                        : throw new CliException($"--to takes next, backlog or a sprint id, not '{destination}'.");
                }

                var closed = await (await c.InWorkspaceAsync(ct)).Sprints[id].Close.PostAsync(new CloseSprintRequest
                {
                    MoveUnfinishedTo = destination == "backlog" ? UnfinishedDestination.Backlog : UnfinishedDestination.Next,
                    Target = target,
                    Items = items,
                    StartNext = c.Get(startNext),
                }, cancellationToken: ct);

                var lines = new List<string> { $"Closed {closed!.Sprint?.Name}." };
                var carried = closed.Outcomes?.FirstOrDefault(o => o.Outcome == CloseOutcome.Carry)?.Count ?? 0;
                var backlogged = closed.Outcomes?.FirstOrDefault(o => o.Outcome == CloseOutcome.Backlog)?.Count ?? 0;
                if (carried > 0)
                {
                    lines.Add(closed.MovedTo is { } next ? $"Carried {carried} issue(s), to {next.Name} unless listed otherwise." : $"Carried {carried} issue(s).");
                }

                if (backlogged > 0)
                {
                    lines.Add($"Moved {backlogged} issue(s) to the backlog.");
                }

                lines.AddRange(closed.Splits?.Select(p => $"Split {p.Original}: {p.HoursDone}h done here, {p.HoursLeft}h in {p.Continuation}.") ?? []);
                if (closed.Skipped is { Count: > 0 } skipped)
                {
                    lines.Add($"Skipped, no longer unfinished in the sprint: {string.Join(", ", skipped)}.");
                }

                if (closed.Started is { } started)
                {
                    lines.Add($"Started {started.Name}.");
                }

                await c.Out.DoneAsync(closed, string.Join(Environment.NewLine, lines));
            });
    }

    private static Command Review()
    {
        var sprint = SprintArgument();
        var project = ActiveProjectOption();
        return new ApiCommand("review", "What closing a sprint needs, or its report once closed.", "getSprintReview") { sprint, project }.Handle(async (c, ct) =>
        {
            var id = await ResolveAsync(c, c.Get(sprint)!, c.Get(project), ct);
            var r = await (await c.InWorkspaceAsync(ct)).Sprints[id].Review.GetAsync(cancellationToken: ct);
            var s = r!.Summary!;
            await c.Out.ItemAsync(r, ("Name", r.Sprint?.Name), ("State", Cli.Dashed(r.Sprint?.State.ToString() ?? "")),
                ("At start", $"{s.AtStart}h"), ("Added", $"{s.Added}h"), ("Removed", $"{s.Removed}h"), ("Re-estimated", $"{s.Reestimated}h"),
                ("Completed", $"{s.Completed}h ({s.CompletionPercent}%)"), ("Carried", $"{s.Carried}h"), ("Split", $"{s.Split}h"),
                ("To backlog", $"{s.ToBacklog}h"), ("Canceled", $"{s.Canceled}h"), ("Remaining", $"{s.Remaining}h"));
            if (c.Out.Json)
            {
                return;
            }

            await c.Out.MessageAsync("");
            await c.Out.MessageAsync("Unfinished:");
            await IssueTableAsync(c, r.Unfinished ?? []);
            await c.Out.MessageAsync("");
            await c.Out.MessageAsync("Completed:");
            await IssueTableAsync(c, r.Completed ?? []);
        });
    }

    private static Command Burndown()
    {
        var sprint = SprintArgument();
        var project = ActiveProjectOption();
        var by = new Option<string?>("--by") { Description = "assignee: also one series per assignee." };
        return new ApiCommand("burndown", "Scope, remaining and ideal hours per day.", "getSprintBurndown") { sprint, project, by }.Handle(async (c, ct) =>
        {
            var id = await ResolveAsync(c, c.Get(sprint)!, c.Get(project), ct);
            var grouping = c.Get(by)?.ToLowerInvariant() switch
            {
                null => (BurndownGrouping?)null,
                "assignee" => BurndownGrouping.Assignee,
                var other => throw new CliException($"--by takes assignee, not '{other}'."),
            };
            var b = await (await c.InWorkspaceAsync(ct)).Sprints[id].Burndown.GetAsync(r => r.QueryParameters.By = grouping, ct);
            if (c.Out.Json)
            {
                await c.Out.DoneAsync(b!, "");
                return;
            }

            var points = b!.Points ?? [];
            if (points.All(p => p.Remaining is null))
            {
                await c.Out.MessageAsync("No burndown data for this sprint.");
                return;
            }

            await c.Out.MessageAsync($"Remaining  {Sparkline(points.Select(p => p.Remaining).ToList())}");
            await c.Out.MessageAsync("");
            var ideal = (b.Ideal ?? []).ToDictionary(i => i.Date?.ToString() ?? "", i => i.Hours);
            await c.Out.ListAsync(points, ["DATE", "SCOPE", "REMAINING", "IDEAL"], p =>
                [p.Date?.ToString(), p.Scope?.ToString(CultureInfo.InvariantCulture), p.Remaining?.ToString(CultureInfo.InvariantCulture),
                    ideal.GetValueOrDefault(p.Date?.ToString() ?? "")?.ToString(CultureInfo.InvariantCulture)]);
            foreach (var a in b.Assignees ?? [])
            {
                await c.Out.MessageAsync("");
                await c.Out.MessageAsync($"{a.Assignee?.Name ?? "Unassigned"}  {Sparkline((a.Points ?? []).Select(p => p.Remaining).ToList())}");
                await c.Out.MessageAsync($"  at start {a.AtStart}h, added {a.Added}h, completed {a.Completed}h, remaining {a.Remaining}h, carried {a.Carried}h");
            }
        });
    }

    private static Task IssueTableAsync(CommandContext c, List<SprintReviewIssue> issues) =>
        c.Out.ListAsync(issues, ["KEY", "STATE", "ASSIGNEE", "HOURS", "OUTCOME", "TITLE"], i =>
            [i.Key, Cli.Dashed(i.State.ToString()!), i.Assignee?.Name, i.Effort?.ToString(CultureInfo.InvariantCulture),
                i.ContinuedIn is { } next ? $"split, continued in {next}" : i.Outcome?.ToString().ToLowerInvariant(), i.Title]);

    /// <summary>One block per day; blank for days without data.</summary>
    internal static string Sparkline(IReadOnlyList<double?> values)
    {
        const string blocks = "▁▂▃▄▅▆▇█";
        var max = values.Max() ?? 0;
        return new string(values.Select(v => v is not { } value ? ' '
            : max <= 0 ? blocks[0]
            : blocks[(int)Math.Round(value / max * (blocks.Length - 1))]).ToArray());
    }

    /// <summary>A sprint id, or <c>active</c> with a project.</summary>
    private static async Task<Guid> ResolveAsync(CommandContext c, string value, string? project, CancellationToken ct)
    {
        if (Guid.TryParse(value, out var id))
        {
            return id;
        }

        if (!value.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException($"Sprint '{value}' isn't a sprint id or 'active'.");
        }

        if (project is null)
        {
            throw new CliException("'active' needs --project.");
        }

        var key = project.ToUpperInvariant();
        var active = await (await c.InWorkspaceAsync(ct)).Sprints.GetAsync(r =>
        {
            r.QueryParameters.Project = key;
            r.QueryParameters.State = [SprintState.Active];
        }, ct);
        return active?.FirstOrDefault()?.Id ?? throw new CliException($"{key} has no active sprint.");
    }

    private static CloseSprintItem ParseSplit(string value)
    {
        var parts = value.Split('=', 2);
        return parts.Length == 2 && double.TryParse(parts[1].TrimEnd('h', 'H'), NumberStyles.Float, CultureInfo.InvariantCulture, out var hours)
            ? new CloseSprintItem { Issue = parts[0], Outcome = CloseOutcome.Split, HoursLeft = hours }
            : throw new CliException($"--split takes KEY=HOURS, e.g. KUP-12=4, not '{value}'.");
    }

    private static IEnumerable<CloseSprintItem> Items(string[]? keys, CloseOutcome outcome) =>
        (keys ?? []).Select(key => new CloseSprintItem { Issue = key, Outcome = outcome });

    private static Argument<string> SprintArgument() => new("sprint") { Description = "Sprint id (from `sprint list`), or `active` with --project." };

    private static Option<string?> ActiveProjectOption() => new("--project", "-p") { Description = "Project key, for `active`." };

    private static Argument<Guid> IdArgument() => new("id") { Description = "Sprint id (from `sprint list`)." };

    private static Date ParseDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", out var date)
            ? new Date(date.Year, date.Month, date.Day)
            : throw new CliException($"Date '{value}' isn't YYYY-MM-DD.");
}
