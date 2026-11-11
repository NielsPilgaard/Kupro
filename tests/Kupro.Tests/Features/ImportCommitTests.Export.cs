using System.Net;
using System.Net.Http.Json;
using Kupro.Api.Features.Attachments;
using Kupro.Api.Features.Comments;
using Kupro.Api.Features.Exports;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Workspaces;
using Kupro.Import.Snapshot;
using Kupro.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kupro.Tests.Features;

/// <summary>A Kupro export ZIP as a source: moving a workspace into another (task 023 acceptance, round trip with task 016).</summary>
public sealed partial class ImportCommitTests
{
    [Fact]
    public async Task A_kupro_export_moves_a_workspace_with_its_keys_history_files_sprints_and_milestones()
    {
        // The workspace to move: an import's worth of history, plus work done in Kupro itself.
        var source = History();
        await ImportedAsync(source, await MappingAsync(source) with { MilestoneVersions = ["v1"] }, files: true);
        await EditAsync("WEB-2", new UpdateIssueRequest(Priority: IssuePriority.Urgent));
        await ReadAsync<CommentResponse>(await _client.PostAsJsonAsync(Url("issues/WEB-1/comments"), new CommentRequest("Written in Kupro"), Json.Options, Ct), HttpStatusCode.Created);
        var before = await IssueAsync("WEB-2");

        var zip = Path.GetTempFileName();
        try
        {
            await WithDbAsync(async db =>
            {
                await using var file = File.Create(zip);
                await ExportWriter.WriteAsync(file, db, Factory.Services.GetRequiredService<ObjectStorage>(), DateTimeOffset.UtcNow, Ct);
            });

            // Into a new, empty workspace.
            (_client, _workspace, _) = await NewWorkspaceAsync("Moved");
            _owner = (await GetAsync<List<MemberResponse>>(_client, Url("members"))).Single().Id;
            var import = (await WithImportsAsync(s => s.CreateAsync(new CreateImportRequest(SourceKinds.Kupro), Ct))).Value!.Id;
            var described = await WithImportsAsync(s => s.StoreExportAsync(import, zip, Ct));
            described.Error.ShouldBeNull();
            described.Value!.Projects.Select(p => p.Key).ShouldBe(["OPS", "WEB"], ignoreOrder: true);

            var mapping = (await WithImportsAsync(s => s.ProposeMappingAsync(import, Ct))).Value!;
            await WithDbAsync(async db =>
                await db.WorkspaceImports.Where(i => i.Id == import).ExecuteUpdateAsync(s => s.SetProperty(i => i.Mapping, mapping.ToJson()), Ct));
            var run = await RunAsync(import, ImportRunKind.Commit);
            run.State.ShouldBe(ImportRunState.Succeeded, run.Error);
        }
        finally
        {
            File.Delete(zip);
        }

        var moved = await IssueAsync("WEB-2");
        (moved.Title, moved.Type, moved.State, moved.Priority, moved.Effort, moved.DueDate).ShouldBe((before.Title, before.Type, before.State, before.Priority, before.Effort, before.DueDate));
        moved.Parent!.Key.ShouldBe("WEB-1");
        moved.Labels.ShouldBe(before.Labels);
        moved.CreatedAt.ShouldBe(before.CreatedAt);
        (await IssueAsync("OPS-1")).Title.ShouldBe("Rotate keys");

        Guid file = default;
        await WithDbAsync(async db =>
        {
            (await db.Comments.Select(c => c.Body).ToListAsync(Ct)).ShouldContain("Written in Kupro");
            (await db.Comments.CountAsync(Ct)).ShouldBe(3);
            (await db.Sprints.Select(s => s.Name).ToListAsync(Ct)).ShouldBe(["Sprint 1"]);
            (await db.Milestones.Select(m => m.Name).ToListAsync(Ct)).ShouldBe(["1.0"]);
            file = (await db.Attachments.SingleAsync(Ct)).Id;
        });
        moved.Description!.ShouldContain($"attachment:{file}");
        await using var stored = await Factory.Services.GetRequiredService<ObjectStorage>().OpenReadAsync($"{_workspace.Id}/{file}", Ct);
        stored.ShouldNotBeNull();

        var feed = await GetAsync<List<Kupro.Api.Features.Activity.FeedItem>>(_client, Url("issues/WEB-2/activity"));
        feed.ShouldContain(f => f.Changes.Any(c => c.Field == "priority" && c.To == "urgent"), "the history made in Kupro comes along");
    }
}
