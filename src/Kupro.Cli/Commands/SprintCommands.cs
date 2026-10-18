using System.CommandLine;
using Kupro.Cli.Api.Models;
using Microsoft.Kiota.Abstractions;

namespace Kupro.Cli.Commands;

/// <summary>sprint list|get|create|update|start|close.</summary>
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
        var id = IdArgument();
        var backlog = new Option<bool>("--to-backlog") { Description = "Move unfinished issues to the backlog instead of the next sprint." };
        return new ApiCommand("close", "Close the active sprint; unfinished issues move to the next sprint.", "closeSprint") { id, backlog }
            .Handle(async (c, ct) =>
            {
                var closed = await (await c.InWorkspaceAsync(ct)).Sprints[c.Get(id)].Close.PostAsync(new CloseSprintRequest
                {
                    MoveUnfinishedTo = c.Get(backlog) ? UnfinishedDestination.Backlog : UnfinishedDestination.Next,
                }, cancellationToken: ct);
                await c.Out.DoneAsync(closed!, $"Closed {closed!.Sprint?.Name}. Moved {closed.Moved} unfinished issue(s) to {closed.MovedTo?.Name ?? "the backlog"}.");
            });
    }

    private static Argument<Guid> IdArgument() => new("id") { Description = "Sprint id (from `sprint list`)." };

    private static Date ParseDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", out var date)
            ? new Date(date.Year, date.Month, date.Day)
            : throw new CliException($"Date '{value}' isn't YYYY-MM-DD.");
}
