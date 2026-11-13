using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Import;
using Kupro.Import.Jira;
using Kupro.Import.Snapshot;
using Kupro.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Tests.Features;

/// <summary>A Jira Cloud site read by the server end to end (task 024): shape, re-hosted images, a delta without duplicates.</summary>
public sealed partial class ImportCommitTests
{
    [Fact]
    public async Task A_jira_import_keeps_the_tree_rehosts_images_and_a_rerun_adds_no_duplicates()
    {
        var jira = JiraConnectorTests.Cloud();
        var connector = new JiraConnector(jira.Client(), jira.SiteUrl, FakeJira.CloudToken, new JiraClock(jira));
        connector.Client.Delay = (_, _) => Task.CompletedTask;
        Serve(connector);

        var created = await WithImportsAsync(s => s.CreateAsync(new CreateImportRequest(FakeKind, Token: "good"), Ct));
        var import = created.Value!.Id;
        var path = Path.GetTempFileName();
        ImportMapping mapping;
        await using (var file = File.Create(path))
        {
            await connector.WriteSnapshotAsync(file, new ReadScope(), null, includeFiles: false, new JiraClock(jira), Ct);
        }

        await using (var file = File.OpenRead(path))
        await using (var reader = await SnapshotReader.OpenAsync(file, Ct))
        {
            mapping = ImportMappingDefaults.Propose(reader.Header, []);
        }

        File.Delete(path);
        await WithDbAsync(async db =>
            await db.WorkspaceImports.Where(i => i.Id == import).ExecuteUpdateAsync(s => s.SetProperty(i => i.Mapping, mapping.ToJson()), Ct));

        await DryRunAsync(import);
        var commit = await RunAsync(import, ImportRunKind.Commit);
        commit.State.ShouldBe(ImportRunState.Succeeded, commit.Error);

        // Initiative › Epic › Story › Sub-task, top folded: Epic › Feature › Task, and the Initiative an Epic of its own.
        (await IssueAsync("WEB-1")).ShouldSatisfyAllConditions(i => i.Type.ShouldBe(IssueType.Epic), i => i.Parent.ShouldBeNull());
        (await IssueAsync("WEB-2")).Type.ShouldBe(IssueType.Epic);
        var story = await IssueAsync("WEB-3");
        story.Type.ShouldBe(IssueType.Feature);
        story.Parent!.Key.ShouldBe("WEB-2");
        story.State.ShouldBe(IssueState.InReview);
        story.Labels.ShouldContain("payments");
        story.Description!.ShouldContain("](attachment:", Case.Sensitive, "the image now opens from Kupro storage");
        story.Description!.ShouldNotContain("atlassian.net/rest", Case.Sensitive);
        story.Description!.ShouldContain("https://acme.atlassian.net/wiki/spaces/PAY/pages/123", Case.Sensitive, "a Confluence link stays a link");
        var task = await IssueAsync("WEB-4");
        task.Type.ShouldBe(IssueType.Task);
        task.Parent!.Key.ShouldBe("WEB-3");

        int count = 0;
        await WithDbAsync(async db => count = await db.Issues.CountAsync(Ct));

        // An edit in Jira after the first run.
        jira.Issues.Single(i => i.Key == "WEB-6").Set("summary", "Apple Pay and Google Pay").Touch(new DateTimeOffset(2025, 3, 7, 9, 0, 0, TimeSpan.Zero));
        var rerun = await RunAsync(import, ImportRunKind.Commit);

        rerun.State.ShouldBe(ImportRunState.Succeeded, rerun.Error);
        (await IssueAsync("WEB-6")).Title.ShouldBe("Apple Pay and Google Pay");
        await WithDbAsync(async db => (await db.Issues.CountAsync(Ct)).ShouldBe(count));
    }

    private sealed class JiraClock(FakeJira jira) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => jira.Now;
    }
}
