using System.Text.Json;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Import.Snapshot;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Tests.Features;

public sealed partial class ImportCommitTests
{
    /// <summary>
    /// Four levels and a Task with children, committed with either fold: only ADR-008 shapes, every item that left its
    /// source parent relates to it, the plan's counts match, and the top keeps its history and aliases.
    /// </summary>
    [Theory]
    [InlineData(FoldEnd.Bottom)]
    [InlineData(FoldEnd.Top)]
    public async Task Four_levels_and_a_task_with_children_commit_valid_shapes_that_match_the_plan(FoldEnd fold)
    {
        var at = new DateTimeOffset(2025, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var source = Source(
            Item("a", key: "WEB-1", type: "epic", title: "Big Initiative", url: "https://src.example/WEB-1"),
            Item("b", key: "WEB-2", type: "feature", parent: "a"),
            Item("c", key: "WEB-3", type: "story", parent: "b"),
            Item("d", key: "WEB-4", type: "task", parent: "c"),
            Item("e", key: "WEB-5", type: "feature", parent: "a"),
            Item("f", key: "WEB-6", type: "task", parent: "e"),
            Item("g", key: "WEB-7", type: "task", parent: "f"));
        source.Records.AddRange(
        [
            new SourcePerson { Id = "u1", Name = "Sofie Larsen" },
            new SourceComment { Id = "c1", Item = "a", Author = "u1", Body = "Kick-off", Markup = MarkupKind.Plain, Created = at.AddDays(1) },
            new SourceChange { Id = "h1", Item = "a", Actor = "u1", At = at.AddDays(2), Field = "title", From = "Initiative", To = "Big Initiative" },
        ]);
        var mapping = await MappingAsync(source) with { Fold = fold };
        var import = await AddImportAsync(source, mapping);

        var dryRun = await RunAsync(import, ImportRunKind.DryRun);
        dryRun.State.ShouldBe(ImportRunState.Succeeded, dryRun.Error);
        var plan = JsonSerializer.Deserialize<ImportPlan>(dryRun.Report!, JsonSerializerOptions.Web)!;
        plan.FourLevels.ShouldBeTrue();
        var counts = fold == FoldEnd.Bottom ? plan.FoldBottom! : plan.FoldTop!;

        var commit = await RunAsync(import, ImportRunKind.Commit);
        commit.State.ShouldBe(ImportRunState.Succeeded, commit.Error);
        Counts(commit).Issues.ShouldBe(plan.Create.Count);

        var sourceParents = source.Records.OfType<SourceItem>().ToDictionary(i => i.Key!, i => i.Parent is null
            ? null
            : source.Records.OfType<SourceItem>().Single(p => p.Id == i.Parent).Key);
        var issues = new Dictionary<string, IssueDetail>();
        foreach (var key in sourceParents.Keys)
        {
            issues[key] = await IssueAsync(key);
        }

        var moved = 0;
        foreach (var (key, issue) in issues)
        {
            if (issue.Parent is { } parent)
            {
                IssueRules.CanBeParent(issue.Type, issues[parent.Key].Type).ShouldBeTrue($"{issue.Type} {key} under {issues[parent.Key].Type} {parent.Key}");
            }

            if (sourceParents[key] is { } old && issue.Parent?.Key != old)
            {
                moved++;
                issue.Relations.ShouldContain(r => r.Type == RelationKind.Relates && r.Issue.Key == old, $"{key} relates to {old}");
            }
        }

        moved.ShouldBeGreaterThan(0);
        moved.ShouldBe(counts.Reparented + counts.FoldedTops);

        // The top item stays an Epic with its comments, history and aliases on either fold.
        var top = issues["WEB-1"];
        top.Type.ShouldBe(IssueType.Epic);
        var feed = await GetAsync<List<FeedItem>>(_client, Url("issues/WEB-1/activity"));
        feed.ShouldContain(f => f.Kind == FeedItemKind.Comment && f.Body == "Kick-off" && f.Actor.Name == "Sofie Larsen");
        feed.ShouldContain(f => f.Kind == FeedItemKind.Updated && f.Changes.Any(c => c.Field == "title" && c.From == "Initiative"));
        await WithDbAsync(async db =>
        {
            var aliases = await db.IssueAliases.Where(a => a.IssueId == top.Id).Select(a => a.Alias).ToListAsync(Ct);
            aliases.ShouldContain("https://src.example/WEB-1");
        });
    }
}
