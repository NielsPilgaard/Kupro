using System.Net;
using System.Text.Json;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Time;
using Kupro.Api.Features.Workspaces;
using Kupro.Import.Snapshot;
using Kupro.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Tests.Features;

/// <summary>Worklogs become time entries when time registration is on (task 034, ADR-021 Import).</summary>
public sealed partial class ImportCommitTests
{
    private static readonly DateOnly WorkDay = new(2025, 3, 3);

    private static FakeConnector Worklogged()
    {
        var logged = new SourceField { Id = "timespent", Name = "Time logged (hours)", Kind = FieldKind.Number, SumsWorklogs = true };
        var source = new FakeConnector(Description() with { Fields = [logged] });
        source.Records.Add(Item("a", key: "WEB-1") with { Fields = new Dictionary<string, JsonElement> { ["timespent"] = FieldValue.Number(43.5m) } });
        source.Records.InsertRange(0, [new SourcePerson { Id = "u1", Name = "Sofie Larsen" }, new SourcePerson { Id = "bot", Name = "Jira Automation" }]);
        source.Records.AddRange(
        [
            new SourceWorklog { Id = "w1", Item = "a", Author = "u1", Date = WorkDay, Minutes = 120, Note = "Pairing" },
            new SourceWorklog { Id = "w2", Item = "a", Author = "u1", Date = WorkDay.AddDays(1), Minutes = 2400 },
            new SourceWorklog { Id = "w3", Item = "a", Author = "bot", Date = WorkDay, Minutes = 60 },
            new SourceWorklog { Id = "w4", Item = "a", Date = WorkDay, Minutes = 30 },
        ]);
        return source;
    }

    private async Task TimeRegistrationAsync(bool on) =>
        await ReadAsync<WorkspaceResponse>(await _client.PatchAsJsonAsync($"/api/v1/workspaces/{_workspace.Slug}",
            new UpdateWorkspaceRequest(TimeRegistration: on), Json.Options, Ct), HttpStatusCode.OK);

    private async Task<Guid> WorklogImportAsync()
    {
        var source = Worklogged();
        var mapping = await MappingAsync(source) with { People = new Dictionary<string, PersonTarget> { ["bot"] = new(PersonAction.Skip) } };
        return await AddImportAsync(source, mapping);
    }

    [Fact]
    public async Task Worklogs_become_time_entries_for_the_person_who_logged_them_as_the_source_recorded_them()
    {
        await TimeRegistrationAsync(true);
        var import = await WorklogImportAsync();

        var run = await RunAsync(import, ImportRunKind.Commit);

        run.State.ShouldBe(ImportRunState.Succeeded, run.Error);
        Counts(run).TimeEntries.ShouldBe(2, "the skipped person's and the authorless worklog stay summed");
        await WithDbAsync(async db =>
        {
            var sofie = await db.Members.Where(m => m.DisplayName == "Sofie Larsen").Select(m => m.Id).SingleAsync(Ct);
            var entries = await db.TimeEntries.OrderBy(e => e.Date).ToListAsync(Ct);
            entries.Select(e => (e.MemberId, e.Date, e.Minutes, e.Note, e.Via)).ShouldBe(
            [
                (sofie, WorkDay, 120, "Pairing", Surface.Import),
                (sofie, WorkDay.AddDays(1), 2400, null, Surface.Import),
            ]);
            (await db.TimeEntryChanges.CountAsync(Ct)).ShouldBe(2);
        });
        (await IssueAsync("WEB-1")).Description!.ShouldContain("| Time logged (hours) | 1.5 |", Case.Sensitive, "only the skipped person's and the authorless hour and a half");

        var again = await RunAsync(import, ImportRunKind.Commit);
        again.State.ShouldBe(ImportRunState.Succeeded, again.Error);
        Counts(again).TimeEntries.ShouldBe(0);
        await WithDbAsync(async db => (await db.TimeEntries.CountAsync(Ct)).ShouldBe(2));

        (await WithImportsAsync(s => s.UndoAsync(import, Ct))).Value!.Undone.ShouldBeTrue();
        await WithDbAsync(async db =>
        {
            (await db.TimeEntries.CountAsync(Ct)).ShouldBe(0);
            (await db.TimeEntryChanges.CountAsync(Ct)).ShouldBe(0);
        });
    }

    [Fact]
    public async Task With_time_registration_off_worklogs_write_no_entries()
    {
        var run = await RunAsync(await WorklogImportAsync(), ImportRunKind.Commit);

        run.State.ShouldBe(ImportRunState.Succeeded, run.Error);
        Counts(run).TimeEntries.ShouldBe(0);
        await WithDbAsync(async db => (await db.TimeEntries.CountAsync(Ct)).ShouldBe(0));
        (await IssueAsync("WEB-1")).Description!.ShouldContain("| Time logged (hours) | 43.5 |");
    }

    [Fact]
    public async Task Undo_waits_while_someone_logged_time_in_kupro_on_an_imported_issue()
    {
        await TimeRegistrationAsync(true);
        var import = await WorklogImportAsync();
        (await RunAsync(import, ImportRunKind.Commit)).State.ShouldBe(ImportRunState.Succeeded);
        await ReadAsync<TimeEntryResponse>(await _client.PostAsJsonAsync(Url("time"),
            new LogTimeRequest("WEB-1", 30), Json.Options, Ct), HttpStatusCode.Created);

        var refused = await WithImportsAsync(s => s.UndoAsync(import, Ct));

        refused.Value!.Undone.ShouldBeFalse();
        refused.Value.Check.Blockers.Select(b => (b.Kind, b.IssueKey)).ShouldBe([("time", "WEB-1")]);
    }
}
