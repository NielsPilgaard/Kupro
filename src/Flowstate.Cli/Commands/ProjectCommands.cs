using System.CommandLine;
using Flowstate.Cli.Api.Models;

namespace Flowstate.Cli.Commands;

/// <summary>project, label.</summary>
internal static class ProjectCommands
{
    public static IEnumerable<Command> Build()
    {
        yield return Project();
        yield return Label();
    }

    private static Command Project()
    {
        var project = new Command("project", "Projects in the workspace.");

        project.Subcommands.Add(new ApiCommand("list", "List projects.", "listProjects").Handle(async (c, ct) =>
        {
            var projects = await (await c.InWorkspaceAsync(ct)).Projects.GetAsync(cancellationToken: ct) ?? [];
            await c.Out.ListAsync(projects, ["KEY", "ISSUES", "NAME"], p => [p.Key, p.IssueCount?.ToString(), p.Name]);
        }));

        var key = new Argument<string>("key") { Description = "Project key, e.g. FLOW." };
        project.Subcommands.Add(new ApiCommand("get", "Show a project.", "getProject") { key }.Handle(async (c, ct) =>
        {
            var p = await (await c.InWorkspaceAsync(ct)).Projects[c.Get(key)].GetAsync(cancellationToken: ct);
            await c.Out.ItemAsync(p!, ("Key", p!.Key), ("Name", p.Name), ("Issues", p.IssueCount?.ToString()),
                ("Colour", Cli.Dashed(p.Color.ToString()!)), ("Description", p.Description));
        }));

        var name = new Option<string?>("--name") { Description = "Project name." };
        var description = new Option<string?>("--description") { Description = "Markdown description." };
        var color = Cli.EnumOption<ProjectColor>("--color", "Colour");
        project.Subcommands.Add(new ApiCommand("create", "Create a project.", "createProject") { key, name, description, color }
            .Handle(async (c, ct) =>
            {
                var p = await (await c.InWorkspaceAsync(ct)).Projects.PostAsync(new CreateProjectRequest
                {
                    Key = c.Get(key)!.ToUpperInvariant(),
                    Name = c.Get(name) ?? c.Get(key),
                    Description = c.Get(description),
                    Color = c.Get(color),
                }, cancellationToken: ct);
                await c.Out.DoneAsync(p!, $"Created project {p!.Key}.");
            }));

        var newKey = new Option<string?>("--key") { Description = "New key (only while the project has no issues)." };
        project.Subcommands.Add(new ApiCommand("update", "Update a project.", "updateProject") { key, newKey, name, description, color }
            .Handle(async (c, ct) =>
            {
                var p = await (await c.InWorkspaceAsync(ct)).Projects[c.Get(key)].PatchAsync(new UpdateProjectRequest
                {
                    Key = c.Get(newKey)?.ToUpperInvariant(),
                    Name = c.Get(name),
                    Description = c.Get(description),
                    Color = c.Get(color),
                }, cancellationToken: ct);
                await c.Out.DoneAsync(p!, $"Updated project {p!.Key}.");
            }));

        return project;
    }

    private static Command Label()
    {
        var label = new Command("label", "Workspace labels (created by using them on an issue).");

        label.Subcommands.Add(new ApiCommand("list", "List labels.", "listLabels").Handle(async (c, ct) =>
        {
            var labels = await (await c.InWorkspaceAsync(ct)).Labels.GetAsync(cancellationToken: ct) ?? [];
            await c.Out.ListAsync(labels, ["ISSUES", "COLOUR", "NAME"],
                l => [l.IssueCount?.ToString(), Cli.Dashed(l.Color.ToString()!), l.Name]);
        }));

        var name = new Argument<string>("name") { Description = "Label name." };
        var rename = new Option<string?>("--name") { Description = "New name." };
        var color = Cli.EnumOption<ProjectColor>("--color", "Colour");
        label.Subcommands.Add(new ApiCommand("update", "Rename or recolour a label.", "updateLabel") { name, rename, color }
            .Handle(async (c, ct) =>
            {
                var l = await (await c.InWorkspaceAsync(ct)).Labels[c.Get(name)].PatchAsync(
                    new UpdateLabelRequest { Name = c.Get(rename), Color = c.Get(color) }, cancellationToken: ct);
                await c.Out.DoneAsync(l!, $"Updated label {l!.Name}.");
            }));

        return label;
    }
}
