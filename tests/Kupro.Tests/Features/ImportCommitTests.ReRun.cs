using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Sprints;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Kupro.Import.Snapshot;
using Kupro.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kupro.Tests.Features;

/// <summary>Re-runs: per-field updates, conflicts, deltas (task 023, ADR-019 Re-runs and conflicts).</summary>
public sealed partial class ImportCommitTests
{
    private async Task<Guid> ImportedAsync(FakeConnector source, ImportMapping? mapping = null)
    {
        var import = await AddImportAsync(source, mapping ?? await MappingAsync(source));
        (await RunAsync(import, ImportRunKind.Commit)).State.ShouldBe(ImportRunState.Succeeded);
        return import;
    }

    private async Task<ImportRun> RerunAsync(Guid import, FakeConnector source, string? cursor = null)
    {
        await StoreAsync(import, source, false, cursor);
        var run = await RunAsync(import, ImportRunKind.Commit);
        run.State.ShouldBe(ImportRunState.Succeeded, run.Error);
        return run;
    }

    private async Task EditAsync(string key, UpdateIssueRequest change) =>
        await ReadAsync<IssueDetail>(await _client.PatchAsJsonAsync(Url($"issues/{key}"), change, Json.Options, Ct), HttpStatusCode.OK);

    private async Task<List<ImportConflict>> ConflictsAsync()
    {
        List<ImportConflict> found = [];
        await WithDbAsync(async db => found = await db.ImportConflicts.AsNoTracking().OrderBy(c => c.Field).ToListAsync(Ct));
        return found;
    }

    private async Task<IssueResult<ImportConflict>> ResolveAsync(Guid import, Guid conflict, bool useSource)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<WorkspaceContext>().SetForSystem(_workspace.Id);
        return await scope.ServiceProvider.GetRequiredService<ImportService>().ResolveConflictAsync(import, conflict, useSource, Ct);
    }

    [Fact]
    public async Task A_rerun_applies_what_only_the_source_changed_and_keeps_what_kupro_changed()
    {
        var import = await ImportedAsync(Source(Item("a", key: "WEB-1", title: "Old title")));
        await EditAsync("WEB-1", new UpdateIssueRequest(Priority: IssuePriority.Urgent));

        var run = await RerunAsync(import, Source(Item("a", key: "WEB-1", title: "New title", status: "done") with { Priority = "Low" }));

        var issue = await IssueAsync("WEB-1");
        issue.Title.ShouldBe("New title");
        issue.State.ShouldBe(IssueState.Done);
        issue.CompletedAt.ShouldNotBeNull();
        issue.Priority.ShouldBe(IssuePriority.Urgent, "Kupro's edit wins");

        var counts = Counts(run);
        (counts.Issues, counts.Updated, counts.Conflicts).ShouldBe((0, 1, 1));
        var conflict = (await ConflictsAsync()).Single();
        conflict.Field.ShouldBe(ImportedField.Priority);
        conflict.State.ShouldBe(ImportConflictState.Open);
        JsonSerializer.Deserialize<ImportRunReport>(run.Report!, JsonSerializerOptions.Web)!.Conflicts.Samples.Single().Key.ShouldBe("WEB-1");

        await WithDbAsync(async db =>
        {
            (await db.Issues.CountAsync(Ct)).ShouldBe(1);
            var entry = await db.Activity.SingleAsync(a => a.Kind == "reimported", Ct);
            entry.Via.ShouldBe(Surface.Import);
            var data = JsonDocument.Parse(entry.Data!).RootElement;
            data.GetProperty("title").GetProperty("to").GetString().ShouldBe("New title");
            data.TryGetProperty("priority", out _).ShouldBeFalse();
        });
    }

    [Fact]
    public async Task A_rerun_with_nothing_new_changes_nothing()
    {
        var source = History();
        var import = await ImportedAsync(source);

        var again = await RerunAsync(import, source);

        (Counts(again).Updated, Counts(again).Conflicts).ShouldBe((0, 0));
        await WithDbAsync(async db => (await db.Activity.CountAsync(a => a.Kind == "reimported", Ct)).ShouldBe(0));
    }

    [Fact]
    public async Task Sides_that_reach_the_same_value_have_no_conflict_and_a_source_going_back_settles_one()
    {
        var import = await ImportedAsync(Source(Item("a", key: "WEB-1", title: "One")));
        await EditAsync("WEB-1", new UpdateIssueRequest(Title: "Two"));

        var agreed = await RerunAsync(import, Source(Item("a", key: "WEB-1", title: "Two")));
        (Counts(agreed).Updated, Counts(agreed).Conflicts).ShouldBe((0, 0));

        await EditAsync("WEB-1", new UpdateIssueRequest(Title: "Mine"));
        (await RerunAsync(import, Source(Item("a", key: "WEB-1", title: "Theirs")))).Report!.ShouldNotBeNull();
        (await ConflictsAsync()).Single().State.ShouldBe(ImportConflictState.Open);

        var back = await RerunAsync(import, Source(Item("a", key: "WEB-1", title: "Two")));
        Counts(back).Conflicts.ShouldBe(0);
        (await ConflictsAsync()).Single().State.ShouldBe(ImportConflictState.Settled);
        (await IssueAsync("WEB-1")).Title.ShouldBe("Mine");
    }

    [Fact]
    public async Task Use_source_applies_it_and_keep_closes_it_and_both_stay_closed_until_the_source_changes_again()
    {
        var import = await ImportedAsync(Source(Item("a", key: "WEB-1", title: "One"), Item("b", key: "WEB-2", title: "Uno")));
        await EditAsync("WEB-1", new UpdateIssueRequest(Title: "Mine"));
        await EditAsync("WEB-2", new UpdateIssueRequest(Title: "Mia"));
        var changed = Source(Item("a", key: "WEB-1", title: "Theirs"), Item("b", key: "WEB-2", title: "Sua"));
        await RerunAsync(import, changed);

        Guid IdOf(IEnumerable<ImportConflict> all, string text) => all.Single(c => c.SourceValue!.Contains(text, StringComparison.Ordinal)).Id;
        var conflicts = await ConflictsAsync();
        (await ResolveAsync(import, IdOf(conflicts, "Theirs"), useSource: true)).Error.ShouldBeNull();
        (await ResolveAsync(import, IdOf(conflicts, "Sua"), useSource: false)).Error.ShouldBeNull();

        (await IssueAsync("WEB-1")).Title.ShouldBe("Theirs");
        (await IssueAsync("WEB-2")).Title.ShouldBe("Mia");
        (await ResolveAsync(import, IdOf(conflicts, "Sua"), useSource: true)).Error!.Code.ShouldBe("conflict_resolved");

        var same = await RerunAsync(import, changed);
        Counts(same).Conflicts.ShouldBe(0, "a resolved conflict stays closed while the source holds still");

        var again = await RerunAsync(import, Source(Item("a", key: "WEB-1", title: "Theirs"), Item("b", key: "WEB-2", title: "Nuova")));
        Counts(again).Conflicts.ShouldBe(1);
        var reopened = (await ConflictsAsync()).Single(c => c.State == ImportConflictState.Open);
        reopened.SourceValue!.ShouldContain("Nuova");
        (await IssueAsync("WEB-2")).Title.ShouldBe("Mia");
    }

    [Fact]
    public async Task A_native_issue_taking_the_next_number_gives_the_new_item_an_alias_not_a_failure()
    {
        var import = await ImportedAsync(Source(Item("a", key: "WEB-1")));
        var native = await ReadAsync<IssueDetail>(await _client.PostAsJsonAsync(Url("issues"), new CreateIssueRequest("WEB", "Native"), Json.Options, Ct), HttpStatusCode.Created);
        native.Key.ShouldBe("WEB-2");

        var run = await RerunAsync(import, Source(Item("a", key: "WEB-1"), Item("b", key: "WEB-2", url: "https://src.example/WEB-2")));

        var counts = Counts(run);
        (counts.Issues, counts.Renumbered).ShouldBe((1, 1));
        await WithDbAsync(async db =>
        {
            var aliases = await db.IssueAliases.Join(db.Issues, a => a.IssueId, x => x.Id, (a, x) => new { a.Alias, x.Key, x.Title }).ToListAsync(Ct);
            aliases.Single(a => a.Alias == "WEB-2").Title.ShouldBe("Item b");
            (await db.Issues.CountAsync(Ct)).ShouldBe(3);
        });
    }

    [Fact]
    public async Task A_delta_only_touches_what_it_lists_and_a_new_child_finds_its_imported_parent()
    {
        var import = await ImportedAsync(Source(Item("e", key: "WEB-1", type: "epic"), Item("s", key: "WEB-2", type: "story", parent: "e", url: "https://src.example/2")));

        var delta = Source(
            Item("t", key: "WEB-3", type: "story", parent: "e") with { Description = "See [two](https://src.example/2)", DescriptionMarkup = MarkupKind.Markdown });
        var run = await RerunAsync(import, delta, cursor: "since");

        var child = await IssueAsync("WEB-3");
        child.Parent!.Key.ShouldBe("WEB-1");
        child.Description!.ShouldStartWith("See two (WEB-2)");
        (await IssueAsync("WEB-2")).Title.ShouldBe("Item s");
        Counts(run).DeletedInSource.ShouldBe(0, "a delta says nothing about what it leaves out");
    }

    [Fact]
    public async Task An_item_a_whole_snapshot_no_longer_has_is_left_alone_and_flagged()
    {
        var import = await ImportedAsync(Source(Item("a", key: "WEB-1"), Item("gone", key: "WEB-2", title: "Deleted over there")));

        var run = await RerunAsync(import, Source(Item("a", key: "WEB-1")));

        Counts(run).DeletedInSource.ShouldBe(1);
        JsonSerializer.Deserialize<ImportRunReport>(run.Report!, JsonSerializerOptions.Web)!.DeletedInSource.Samples.Single().Key.ShouldBe("WEB-2");
        (await IssueAsync("WEB-2")).Title.ShouldBe("Deleted over there");
    }

    [Fact]
    public async Task A_changed_fold_leaves_imported_items_in_their_shape()
    {
        var source = Source(
            Item("a", key: "WEB-1", type: "epic"),
            Item("b", key: "WEB-2", type: "feature", parent: "a"),
            Item("c", key: "WEB-3", type: "story", parent: "b"),
            Item("d", key: "WEB-4", type: "task", parent: "c"));
        var mapping = await MappingAsync(source);
        var import = await ImportedAsync(source, mapping);
        await WithDbAsync(async db =>
        {
            var row = await db.WorkspaceImports.SingleAsync(i => i.Id == import, Ct);
            row.Mapping = (mapping with { Fold = FoldEnd.Top }).ToJson();
            await db.SaveChangesAsync(Ct);
        });

        var run = await RerunAsync(import, source);

        (Counts(run).Updated, Counts(run).Conflicts).ShouldBe((0, 0));
        (await IssueAsync("WEB-2")).Type.ShouldBe(IssueType.Feature);
        (await IssueAsync("WEB-2")).Parent!.Key.ShouldBe("WEB-1");
        (await IssueAsync("WEB-4")).Parent!.Key.ShouldBe("WEB-2");
    }

    [Fact]
    public async Task A_new_child_under_an_imported_task_goes_up_and_the_task_stays_a_task()
    {
        var import = await ImportedAsync(Source(Item("e", key: "WEB-1", type: "epic"), Item("t", key: "WEB-2", type: "task", parent: "e")));

        await RerunAsync(import, Source(Item("n", key: "WEB-3", type: "task", parent: "t")), cursor: "since");

        (await IssueAsync("WEB-2")).Type.ShouldBe(IssueType.Task);
        var child = await IssueAsync("WEB-3");
        child.Parent!.Key.ShouldBe("WEB-1");
        child.Relations.ShouldContain(r => r.Type == RelationKind.Relates && r.Issue.Key == "WEB-2");
    }

    [Fact]
    public async Task An_item_the_source_moved_moves_when_kupro_left_it_alone()
    {
        var import = await ImportedAsync(Source(
            Item("e1", key: "WEB-1", type: "epic"), Item("e2", key: "WEB-2", type: "epic"), Item("t", key: "WEB-3", type: "task", parent: "e1")));

        var run = await RerunAsync(import, Source(Item("t", key: "WEB-3", type: "task", parent: "e2")), cursor: "since");

        Counts(run).Updated.ShouldBe(1);
        (await IssueAsync("WEB-3")).Parent!.Key.ShouldBe("WEB-2");
    }

    [Fact]
    public async Task A_holder_keeps_its_checklist_lines_in_place_when_a_delta_lists_only_some()
    {
        var description = Description();
        FakeConnector Card(params SourceItem[] lines)
        {
            var source = new FakeConnector(description with { ItemTypes = [.. description.ItemTypes, new SourceItemType { Id = "check", Name = "Checklist item" }] });
            source.Records.Add(Item("card", key: "WEB-1"));
            source.Records.AddRange(lines);
            return source;
        }

        var import = await ImportedAsync(Card(
            Item("c1", type: "check", parent: "card", title: "First"), Item("c2", type: "check", parent: "card", title: "Second")));

        var run = await RerunAsync(import, Card(Item("c2", type: "check", parent: "card", title: "Second", status: "done")), cursor: "since");

        Counts(run).Updated.ShouldBe(1);
        var text = (await IssueAsync("WEB-1")).Description!;
        text.ShouldContain("- [ ] First\n- [x] Second");
    }

    [Fact]
    public async Task A_rerun_inside_an_active_sprint_writes_the_ledger()
    {
        var source = Source(Item("a", key: "WEB-1") with { Sprint = "sp", EstimateSeconds = 3600 });
        source.Records.Insert(0, new SourceSprint { Id = "sp", Name = "Now", Project = "p", Start = new DateOnly(2030, 1, 1), End = new DateOnly(2030, 1, 14) });
        var import = await ImportedAsync(source);
        await WithDbAsync(async db =>
        {
            var sprint = await db.Sprints.SingleAsync(Ct);
            sprint.State = SprintState.Active;
            await db.SaveChangesAsync(Ct);
        });

        var changed = Source(Item("a", key: "WEB-1") with { Sprint = "sp", EstimateSeconds = 7200 });
        changed.Records.Insert(0, new SourceSprint { Id = "sp", Name = "Now", Project = "p", Start = new DateOnly(2030, 1, 1), End = new DateOnly(2030, 1, 14) });
        await RerunAsync(import, changed);

        await WithDbAsync(async db => (await db.SprintLedger.SumAsync(l => l.ScopeDelta, Ct)).ShouldBe(1));
    }

    [Fact]
    public async Task Merging_a_placeholder_does_not_read_as_an_edit_on_the_next_run()
    {
        var source = Source(Item("a", key: "WEB-1", assignee: "u1"));
        source.Records.Insert(0, new SourcePerson { Id = "u1", Name = "Sofie Larsen" });
        var import = await ImportedAsync(source);
        var placeholder = (await GetAsync<List<MemberResponse>>(_client, Url("members"))).Single(m => m.Status == MemberStatus.Placeholder);

        (await _client.PostAsJsonAsync(Url($"members/{placeholder.Id}/merge"), new MergePlaceholderRequest(_owner), Json.Options, Ct)).IsSuccessStatusCode.ShouldBeTrue();

        var run = await RerunAsync(import, source);

        (Counts(run).Updated, Counts(run).Conflicts).ShouldBe((0, 0));
        (await IssueAsync("WEB-1")).Assignee!.Id.ShouldBe(_owner);
    }

    [Fact]
    public async Task Use_source_refuses_a_value_that_no_longer_fits()
    {
        var import = await ImportedAsync(Source(Item("a", key: "WEB-1") with { Due = new DateOnly(2030, 1, 10) }));
        await EditAsync("WEB-1", new UpdateIssueRequest(DueDate: new DateOnly(2030, 1, 20)));
        await RerunAsync(import, Source(Item("a", key: "WEB-1") with { Due = new DateOnly(2030, 1, 5) }));
        await EditAsync("WEB-1", new UpdateIssueRequest(StartDate: new DateOnly(2030, 1, 15)));

        var refused = await ResolveAsync(import, (await ConflictsAsync()).Single().Id, useSource: true);

        refused.Error!.Code.ShouldBe("invalid_dates");
        (await ConflictsAsync()).Single().State.ShouldBe(ImportConflictState.Open);
    }
}
