using System.CommandLine;
using Flowstate.Cli.Api.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Flowstate.Cli.Commands;

/// <summary>issue list|search|get|create|create-many|update|update-many|move|close|relate|unrelate.</summary>
internal static class IssueCommands
{
    /// <summary>Value that empties a field in <c>issue update</c>.</summary>
    private const string None = "none";

    public static IEnumerable<Command> Build()
    {
        var issue = new Command("issue", "Issues: list, search, read, create, update, move, relate.");
        issue.Subcommands.Add(List(search: false));
        issue.Subcommands.Add(List(search: true));
        issue.Subcommands.Add(Get());
        issue.Subcommands.Add(Create());
        issue.Subcommands.Add(CreateMany());
        issue.Subcommands.Add(Update());
        issue.Subcommands.Add(UpdateMany());
        issue.Subcommands.Add(Move());
        issue.Subcommands.Add(Close());
        issue.Subcommands.Add(Relate());
        issue.Subcommands.Add(Unrelate());
        yield return issue;
    }

    /// <summary><c>issue list [-q query]</c>, or <c>issue search words...</c> (the same operation, query as arguments).</summary>
    private static Command List(bool search)
    {
        var query = new Option<string?>("--query", "-q") { Description = "Search text and filters, e.g. \"drag epics type:feature state:todo assignee:me\"." };
        var words = new Argument<string[]>("query") { Description = "Search text and filters, e.g. drag epics state:todo assignee:me.", Arity = ArgumentArity.OneOrMore };
        var project = new Option<string[]>("--project", "-p") { Description = "Project key; repeat for several.", AllowMultipleArgumentsPerToken = true };
        var state = Cli.EnumListOption<IssueState>("--state", "State");
        var type = Cli.EnumListOption<IssueType>("--type", "Type");
        var assignee = new Option<string?>("--assignee") { Description = "Member id, `me`, or `none`." };
        var label = new Option<string?>("--label") { Description = "Label name." };
        var parent = new Option<string?>("--parent") { Description = "Parent issue key: list its children." };
        var sprint = new Option<string?>("--sprint") { Description = "Sprint id, `active` or `none`." };
        var sort = Cli.EnumOption<IssueSort>("--sort", "Order (default: relevance when searching, else updated)");
        var limit = new Option<int?>("--limit") { Description = "Page size, 1-200 (default 50)." };
        var cursor = new Option<string?>("--cursor") { Description = "Continue from a previous page's nextCursor." };
        var all = new Option<bool>("--all") { Description = "Follow pages until the end." };
        var command = search
            ? new ApiCommand("search", "Search issues: free text (typo tolerant) plus key:value filters.", "listIssues") { words }
            : new ApiCommand("list", "List issues with filters.", "listIssues") { query };
        foreach (var option in new Option[] { project, state, type, assignee, label, parent, sprint, sort, limit, cursor, all })
        {
            command.Options.Add(option);
        }

        return command.Handle(async (c, ct) =>
        {
            var issues = (await c.InWorkspaceAsync(ct)).Issues;
            var items = new List<IssueSummary>();
            var next = c.Get(cursor);
            IssuePage? page;
            do
            {
                var pageCursor = next;
                page = await issues.GetAsync(r =>
                {
                    var q = r.QueryParameters;
                    q.Q = search ? string.Join(' ', c.Get(words) ?? []) : c.Get(query);
                    q.Project = c.Get(project) is { Length: > 0 } p ? p : null;
                    q.State = c.Get(state) is { Length: > 0 } s ? s : null;
                    q.Type = c.Get(type) is { Length: > 0 } t ? t : null;
                    q.Assignee = c.Get(assignee);
                    q.Label = c.Get(label);
                    q.Parent = c.Get(parent);
                    q.Sprint = c.Get(sprint);
                    q.Sort = c.Get(sort);
                    q.Limit = c.Get(limit);
                    q.Cursor = pageCursor;
                }, ct);
                items.AddRange(page?.Items ?? []);
                next = page?.NextCursor;
            }
            while (c.Get(all) && next is not null);

            if (c.Out.Json)
            {
                await c.Out.ItemAsync(new IssuePage { Items = items, NextCursor = c.Get(all) ? null : next });
                return;
            }

            await c.Out.ListAsync(items, ["KEY", "STATE", "PRIORITY", "ASSIGNEE", "LABELS", "TITLE"], i =>
                [i.Key, Cli.Dashed(i.State.ToString()!), i.Priority == IssuePriority.None ? "" : Cli.Dashed(i.Priority.ToString()!),
                    i.Assignee?.Name, string.Join(",", i.Labels ?? []),
                    (i.Type == IssueType.Task ? "" : $"[{i.Type}] ") + i.Title]);
            if (next is not null)
            {
                await c.Out.MessageAsync($"More: --cursor {next} (or --all)");
            }
        });
    }

    private static Command Get()
    {
        var key = KeyArgument();
        return new ApiCommand("get", "Show an issue with its description, children and relations.", "getIssue") { key }
            .Handle(async (c, ct) =>
            {
                var i = await (await c.InWorkspaceAsync(ct)).Issues[c.Get(key)].GetAsync(cancellationToken: ct);
                await c.Out.ItemAsync(i!,
                    ("Key", i!.Key),
                    ("Title", i.Title),
                    ("Type", Cli.Dashed(i.Type.ToString()!)),
                    ("State", Cli.Dashed(i.State.ToString()!)),
                    ("Priority", Cli.Dashed(i.Priority.ToString()!)),
                    ("Assignee", i.Assignee?.Name),
                    ("Parent", i.Parent is { } p ? $"{p.Key} {p.Title}" : null),
                    ("Effort", i.Effort is { } e ? $"{e}h" + (i.ChildEffort is { } ce ? $" (children {ce}h)" : "") : null),
                    ("Start", i.StartDate?.ToString()),
                    ("Due", i.DueDate?.ToString()),
                    ("Labels", string.Join(", ", i.Labels ?? [])),
                    ("Children", string.Join("\n" + new string(' ', 11), (i.Children ?? []).Select(ch => $"{ch.Key} [{Cli.Dashed(ch.State.ToString()!)}] {ch.Title}"))),
                    ("Relations", string.Join("\n" + new string(' ', 11), (i.Relations ?? []).Select(r => $"{Cli.Dashed(r.Type.ToString()!)} {r.Issue?.Key} {r.Issue?.Title}"))),
                    ("Created by", i.CreatedBy?.Name),
                    ("Description", i.Description is { } d ? "\n" + d : null));
            });
    }

    private static Command Create()
    {
        var project = new Option<string>("--project", "-p") { Description = "Project key.", Required = true };
        var title = new Option<string>("--title", "-t") { Description = "Title.", Required = true };
        var f = new IssueFieldOptions();
        var command = new ApiCommand("create", "Create an issue.", "createIssue") { project, title };
        f.AddTo(command);

        return command.Handle(async (c, ct) =>
        {
            var request = new CreateIssueRequest
            {
                Project = c.Get(project)!.ToUpperInvariant(),
                Title = c.Get(title),
                Type = c.Get(f.Type),
                Description = await f.DescriptionAsync(c, ct),
                State = c.Get(f.State),
                Priority = c.Get(f.Priority),
                Assignee = c.Get(f.Assignee),
                Parent = c.Get(f.Parent),
                Effort = ParseEffort(c.Get(f.Effort)),
                StartDate = ParseDate(c.Get(f.Start)),
                DueDate = ParseDate(c.Get(f.Due)),
                Labels = c.Get(f.Labels) is { Length: > 0 } l ? [.. l] : null,
                Sprint = c.Get(f.Sprint),
            };
            var created = await (await c.InWorkspaceAsync(ct)).Issues.PostAsync(request, cancellationToken: ct);
            await c.Out.DoneAsync(created!, $"Created {created!.Key}: {created.Title}");
        });
    }

    private static Command CreateMany()
    {
        var file = FileOption("JSON: { \"issues\": [CreateIssueRequest, ...] } (up to 100).");
        return new ApiCommand("create-many", "Create up to 100 issues from JSON, all-or-nothing.", "createIssues") { file }
            .Handle(async (c, ct) =>
            {
                var request = await ReadJsonAsync(c.Get(file)!, CreateIssuesRequest.CreateFromDiscriminatorValue, ct);
                var created = await (await c.InWorkspaceAsync(ct)).Issues.Bulk.PostAsync(request, cancellationToken: ct) ?? [];
                await c.Out.ListAsync(created, ["KEY", "TITLE"], i => [i.Key, i.Title]);
            });
    }

    private static Command Update()
    {
        var key = KeyArgument();
        var title = new Option<string?>("--title", "-t") { Description = "New title." };
        var f = new IssueFieldOptions(forUpdate: true);
        var add = new Option<string[]>("--add-label") { Description = "Add a label; repeatable.", AllowMultipleArgumentsPerToken = true };
        var remove = new Option<string[]>("--remove-label") { Description = "Remove a label; repeatable.", AllowMultipleArgumentsPerToken = true };
        var command = new ApiCommand("update", "Update an issue. Pass `none` to empty assignee, parent, effort or dates.", "updateIssue")
            { key, title, add, remove };
        f.AddTo(command);

        return command.Handle(async (c, ct) =>
        {
            var clear = new List<IssueField?>();
            string? Field(Option<string?> option, IssueField field)
            {
                var value = c.Get(option);
                if (value is not null && value.Equals(None, StringComparison.OrdinalIgnoreCase))
                {
                    clear.Add(field);
                    return null;
                }

                return value;
            }

            var description = await f.DescriptionAsync(c, ct);
            var request = new UpdateIssueRequest
            {
                Title = c.Get(title),
                Type = c.Get(f.Type),
                Description = description,
                State = c.Get(f.State),
                Priority = c.Get(f.Priority),
                Assignee = Field(f.Assignee, IssueField.Assignee),
                Parent = Field(f.Parent, IssueField.Parent),
                Effort = ParseEffort(Field(f.Effort, IssueField.Effort)),
                StartDate = ParseDate(Field(f.Start, IssueField.StartDate)),
                DueDate = ParseDate(Field(f.Due, IssueField.DueDate)),
                Labels = c.Get(f.Labels) is { Length: > 0 } l ? [.. l] : null,
                AddLabels = c.Get(add) is { Length: > 0 } a ? [.. a] : null,
                RemoveLabels = c.Get(remove) is { Length: > 0 } r ? [.. r] : null,
                Sprint = Field(f.Sprint, IssueField.Sprint),
                Clear = clear.Count > 0 ? clear : null,
            };
            var updated = await (await c.InWorkspaceAsync(ct)).Issues[c.Get(key)].PatchAsync(request, cancellationToken: ct);
            await c.Out.DoneAsync(updated!, $"Updated {updated!.Key}.");
        });
    }

    private static Command UpdateMany()
    {
        var file = FileOption("JSON: { \"items\": [{ \"key\": \"FLOW-1\", \"changes\": UpdateIssueRequest }, ...] } (up to 100).");
        return new ApiCommand("update-many", "Apply up to 100 updates from JSON, all-or-nothing.", "updateIssues") { file }
            .Handle(async (c, ct) =>
            {
                var request = await ReadJsonAsync(c.Get(file)!, UpdateIssuesRequest.CreateFromDiscriminatorValue, ct);
                var updated = await (await c.InWorkspaceAsync(ct)).Issues.Bulk.PatchAsync(request, cancellationToken: ct) ?? [];
                await c.Out.ListAsync(updated, ["KEY", "STATE", "TITLE"], i => [i.Key, Cli.Dashed(i.State.ToString()!), i.Title]);
            });
    }

    private static Command Move()
    {
        var key = KeyArgument();
        var state = Cli.EnumOption<IssueState>("--state", "Target column");
        var after = new Option<string?>("--after") { Description = "Place directly below this issue." };
        var before = new Option<string?>("--before") { Description = "Place directly above this issue." };
        return new ApiCommand("move", "Move an issue on the board (state and/or position).", "moveIssue") { key, state, after, before }
            .Handle(async (c, ct) =>
            {
                var moved = await (await c.InWorkspaceAsync(ct)).Issues[c.Get(key)].Move.PostAsync(
                    new MoveIssueRequest { State = c.Get(state), After = c.Get(after), Before = c.Get(before) }, cancellationToken: ct);
                await c.Out.DoneAsync(moved!, $"Moved {moved!.Key} to {Cli.Dashed(moved.State.ToString()!)}.");
            });
    }

    private static Command Close()
    {
        var key = KeyArgument();
        var canceled = new Option<bool>("--canceled") { Description = "Close as canceled instead of done." };
        return new ApiCommand("close", "Mark an issue done (or canceled).", "updateIssue") { key, canceled }
            .Handle(async (c, ct) =>
            {
                var state = c.Get(canceled) ? IssueState.Canceled : IssueState.Done;
                var closed = await (await c.InWorkspaceAsync(ct)).Issues[c.Get(key)].PatchAsync(
                    new UpdateIssueRequest { State = state }, cancellationToken: ct);
                await c.Out.DoneAsync(closed!, $"Closed {closed!.Key} as {Cli.Dashed(state.ToString())}.");
            });
    }

    private static Command Relate()
    {
        var key = KeyArgument();
        var type = Cli.EnumArgument<RelationKind>("relation", "How the issue relates to the target");
        var target = new Argument<string>("target") { Description = "The other issue's key." };
        return new ApiCommand("relate", "Relate two issues, e.g. `issue relate FLOW-1 blocks FLOW-2`.", "addIssueRelation")
            { key, type, target }.Handle(async (c, ct) =>
        {
            var relation = await (await c.InWorkspaceAsync(ct)).Issues[c.Get(key)].Relations.PostAsync(
                new AddRelationRequest { Type = c.Get(type), Target = c.Get(target) }, cancellationToken: ct);
            await c.Out.DoneAsync(relation!, $"{c.Get(key)} {Cli.Dashed(relation!.Type.ToString()!)} {relation.Issue?.Key} (relation {relation.Id}).");
        });
    }

    private static Command Unrelate()
    {
        var key = KeyArgument();
        var id = new Argument<Guid>("relation-id") { Description = "Relation id (from `issue get --json`)." };
        return new ApiCommand("unrelate", "Remove a relation.", "removeIssueRelation") { key, id }.Handle(async (c, ct) =>
        {
            await (await c.InWorkspaceAsync(ct)).Issues[c.Get(key)].Relations[c.Get(id)].DeleteAsync(cancellationToken: ct);
            await c.Out.MessageAsync("Relation removed.");
        });
    }

    private static Argument<string> KeyArgument() => new("key") { Description = "Issue key, e.g. FLOW-12." };

    private static Option<string> FileOption(string description) =>
        new("--file", "-f") { Description = $"Path, or - for stdin. {description}", Required = true };

    private static async Task<T> ReadJsonAsync<T>(string path, ParsableFactory<T> factory, CancellationToken ct) where T : IParsable
    {
        var json = path == "-" ? await Console.In.ReadToEndAsync(ct) : await File.ReadAllTextAsync(path, ct);
        try
        {
            return await KiotaJsonSerializer.DeserializeAsync(json, factory, ct)
                ?? throw new CliException("The JSON file is empty.");
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new CliException($"The file isn't valid JSON: {e.Message}");
        }
    }

    private static double? ParseEffort(string? value) =>
        value is null ? null
        : double.TryParse(value.TrimEnd('h', 'H'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var hours) ? hours
        : throw new CliException($"Effort '{value}' isn't a number of hours.");

    private static Date? ParseDate(string? value) =>
        value is null ? null
        : DateOnly.TryParseExact(value, "yyyy-MM-dd", out var date) ? new Date(date.Year, date.Month, date.Day)
        : throw new CliException($"Date '{value}' isn't YYYY-MM-DD.");

    /// <summary>Options shared by create and update.</summary>
    private sealed class IssueFieldOptions(bool forUpdate = false)
    {
        private readonly string _clearHint = forUpdate ? " `none` empties it." : "";

        public Option<IssueType?> Type { get; } = Cli.EnumOption<IssueType>("--type", "Type");
        public Option<IssueState?> State { get; } = Cli.EnumOption<IssueState>("--state", "State");
        public Option<IssuePriority?> Priority { get; } = Cli.EnumOption<IssuePriority>("--priority", "Priority");
        public Option<string?> Description { get; } = new("--description", "-d") { Description = "Markdown description." };
        public Option<string?> DescriptionFile { get; } = new("--description-file") { Description = "Read the description from a file, or - for stdin." };
        public Option<string?> Assignee { get; } = new("--assignee", "-a") { Description = "Member id or `me`." };
        public Option<string?> Parent { get; } = new("--parent") { Description = "Parent issue key." };
        public Option<string?> Effort { get; } = new("--effort", "-e") { Description = "Estimate in hours, e.g. 6 or 1.5." };
        public Option<string?> Start { get; } = new("--start") { Description = "Start date, YYYY-MM-DD." };
        public Option<string?> Due { get; } = new("--due") { Description = "Due date, YYYY-MM-DD." };
        public Option<string?> Sprint { get; } = new("--sprint") { Description = "Sprint id or `active`." };
        public Option<string[]> Labels { get; } = new("--label", "-l") { Description = "Label; repeatable.", AllowMultipleArgumentsPerToken = true };

        public void AddTo(Command command)
        {
            foreach (var option in new[] { Assignee, Parent, Effort, Start, Due, Sprint })
            {
                option.Description += _clearHint;
            }

            if (forUpdate)
            {
                Labels.Description = "Replace all labels; repeatable.";
            }

            command.Options.Add(Type);
            command.Options.Add(State);
            command.Options.Add(Priority);
            command.Options.Add(Description);
            command.Options.Add(DescriptionFile);
            command.Options.Add(Assignee);
            command.Options.Add(Parent);
            command.Options.Add(Effort);
            command.Options.Add(Start);
            command.Options.Add(Due);
            command.Options.Add(Labels);
            command.Options.Add(Sprint);
        }

        public async Task<string?> DescriptionAsync(CommandContext c, CancellationToken ct) =>
            c.Get(DescriptionFile) is { } path
                ? path == "-" ? await Console.In.ReadToEndAsync(ct) : await File.ReadAllTextAsync(path, ct)
                : c.Get(Description);
    }
}
