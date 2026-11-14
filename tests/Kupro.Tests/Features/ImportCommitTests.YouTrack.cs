using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Import;
using Kupro.Import.Snapshot;
using Kupro.Import.YouTrack;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Tests.Features;

/// <summary>A YouTrack project read by the server end to end (task 027): keys kept, Estimation in hours, resolved states.</summary>
public sealed partial class ImportCommitTests
{
    [Fact]
    public async Task A_youtrack_import_keeps_keys_estimation_and_subtask_trees()
    {
        var youtrack = YouTrackConnectorTests.Sample();
        var connector = new YouTrackConnector(new HttpClient(youtrack), youtrack.Site, Infrastructure.FakeYouTrack.Token);
        connector.Client.Delay = (_, _) => Task.CompletedTask;
        Serve(connector);

        var created = await WithImportsAsync(s => s.CreateAsync(new CreateImportRequest(FakeKind, Token: "good"), Ct));
        var import = created.Value!.Id;
        var stream = new MemoryStream();
        await connector.WriteSnapshotAsync(stream, new ReadScope { Projects = ["0-1"] }, null, includeFiles: false, TimeProvider.System, Ct);
        stream.Position = 0;
        ImportMapping mapping;
        await using (var reader = await SnapshotReader.OpenAsync(stream, Ct))
        {
            mapping = ImportMappingDefaults.Propose(reader.Header, []);
        }

        mapping = mapping with { Scope = mapping.Scope with { Projects = ["0-1"] } };
        await WithDbAsync(async db =>
            await db.WorkspaceImports.Where(i => i.Id == import).ExecuteUpdateAsync(s => s.SetProperty(i => i.Mapping, mapping.ToJson()), Ct));

        await DryRunAsync(import);
        var commit = await RunAsync(import, ImportRunKind.Commit);
        commit.State.ShouldBe(ImportRunState.Succeeded, commit.Error);

        var epic = await IssueAsync("WEB-1");
        epic.Type.ShouldBe(IssueType.Epic);
        epic.State.ShouldBe(IssueState.InProgress);
        var bug = await IssueAsync("WEB-2");
        bug.Parent!.Key.ShouldBe("WEB-1");
        bug.State.ShouldBe(IssueState.Done);
        bug.Priority.ShouldBe(IssuePriority.Urgent);
        bug.Effort.ShouldBe(1.5m, "90 minutes of Estimation");
        bug.DueDate.ShouldBe(new DateOnly(2025, 3, 20));
        bug.Labels.ShouldContain("payments");
        (await IssueAsync("WEB-3")).State.ShouldBe(IssueState.Canceled, "Obsolete is dropped work");
        await WithDbAsync(async db =>
        {
            var aliases = await db.IssueAliases.Where(a => a.ImportId == import).Select(a => a.Alias).ToListAsync(Ct);
            aliases.ShouldContain("https://acme.youtrack.cloud/issue/WEB-2");
        });
    }
}
