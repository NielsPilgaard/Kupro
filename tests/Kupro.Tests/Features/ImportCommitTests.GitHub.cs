using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Import;
using Kupro.Import.GitHub;
using Kupro.Import.Snapshot;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Tests.Features;

/// <summary>A GitHub repository read by the server end to end (task 027): numbers kept, sub-issues fit, close reasons, aliases.</summary>
public sealed partial class ImportCommitTests
{
    [Fact]
    public async Task A_github_import_keeps_issue_numbers_and_answers_to_owner_repo_references()
    {
        var github = GitHubConnectorTests.Sample();
        var connector = new GitHubConnector(new HttpClient(github), null, Infrastructure.FakeGitHub.Token);
        connector.Client.Delay = (_, _) => Task.CompletedTask;
        Serve(connector);

        var created = await WithImportsAsync(s => s.CreateAsync(new CreateImportRequest(FakeKind, Token: "good"), Ct));
        var import = created.Value!.Id;
        var stream = new MemoryStream();
        await connector.WriteSnapshotAsync(stream, new ReadScope { Projects = ["100"] }, null, includeFiles: false, TimeProvider.System, Ct);
        stream.Position = 0;
        ImportMapping mapping;
        await using (var reader = await SnapshotReader.OpenAsync(stream, Ct))
        {
            mapping = ImportMappingDefaults.Propose(reader.Header, []);
        }

        mapping = mapping with { Scope = mapping.Scope with { Projects = ["100"] } };
        await WithDbAsync(async db =>
            await db.WorkspaceImports.Where(i => i.Id == import).ExecuteUpdateAsync(s => s.SetProperty(i => i.Mapping, mapping.ToJson()), Ct));

        await DryRunAsync(import);
        var commit = await RunAsync(import, ImportRunKind.Commit);
        commit.State.ShouldBe(ImportRunState.Succeeded, commit.Error);

        (await IssueAsync("API-1")).Type.ShouldBe(IssueType.Feature);
        var bug = await IssueAsync("API-2");
        bug.Title.ShouldBe("Pay twice fails");
        bug.Parent!.Key.ShouldBe("API-1");
        bug.State.ShouldBe(IssueState.Done);
        bug.Labels.ShouldContain("bug");
        (await IssueAsync("API-3")).State.ShouldBe(IssueState.Canceled, "closed as not planned");
        (await IssueAsync("API-5")).State.ShouldBe(IssueState.Canceled, "closed as duplicate");
        await WithDbAsync(async db =>
        {
            (await db.Issues.AnyAsync(i => i.Title == "Fix pay", Ct)).ShouldBeFalse("a pull request is not an issue");
            var aliases = await db.IssueAliases.Where(a => a.ImportId == import).Select(a => a.Alias).ToListAsync(Ct);
            aliases.ShouldContain("acme/api#2");
            aliases.ShouldContain("https://github.com/acme/api/issues/2");
        });
    }
}
