using System.Net;
using Kupro.Api.Features.Attachments;
using Kupro.Api.Features.Comments;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Kupro.Import.Snapshot;
using Kupro.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kupro.Tests.Features;

/// <summary>Invites, Undo and Finish (task 023, ADR-019 People and Re-runs and conflicts).</summary>
public sealed partial class ImportCommitTests
{
    private async Task<T> WithImportsAsync<T>(Func<ImportService, Task<T>> use)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WorkspaceContext>();
        context.SetForSystem(_workspace.Id);

        // As the owner, the way the REST surface will call it.
        using var owner = context.ActAs(_owner, Kupro.Api.Features.Activity.Surface.Api, Guid.Empty);
        return await use(scope.ServiceProvider.GetRequiredService<ImportService>());
    }

    [Fact]
    public async Task Undo_removes_everything_the_import_made_and_nothing_else()
    {
        await ReadAsync<ProjectResponse>(await _client.PostAsJsonAsync(Url("projects"), new CreateProjectRequest("OWN", "Ours"), Json.Options, Ct), HttpStatusCode.Created);
        await ReadAsync<IssueDetail>(await _client.PostAsJsonAsync(Url("issues"), new CreateIssueRequest("OWN", "Native", Labels: ["payments"]), Json.Options, Ct), HttpStatusCode.Created);
        var source = History();
        var import = await AddImportAsync(source, await MappingAsync(source) with { MilestoneVersions = ["v1"] }, files: true);
        (await RunAsync(import, ImportRunKind.Commit)).State.ShouldBe(ImportRunState.Succeeded);
        await EditAsync("WEB-2", new UpdateIssueRequest(Title: "Edited here"));
        Guid file = default;
        await WithDbAsync(async db => file = (await db.Attachments.SingleAsync(Ct)).Id);

        var check = await WithImportsAsync(s => s.CheckUndoAsync(import, Ct));
        check.Value!.Edited.ShouldBe(["WEB-2"]);
        check.Value.BlockerCount.ShouldBe(0);

        var undone = await WithImportsAsync(s => s.UndoAsync(import, Ct));

        undone.Value!.Undone.ShouldBeTrue();
        undone.Value.Issues.ShouldBe(3);
        await WithDbAsync(async db =>
        {
            (await db.Issues.Select(i => i.Key).ToListAsync(Ct)).ShouldBe(["OWN-1"]);
            (await db.Projects.Select(p => p.Key).ToListAsync(Ct)).ShouldBe(["OWN"]);
            (await db.Labels.Select(l => l.Name).ToListAsync(Ct)).ShouldBe(["payments"], "a label native work uses stays");
            (await db.Members.CountAsync(m => m.Status == MemberStatus.Placeholder, Ct)).ShouldBe(0);
            (await db.Sprints.CountAsync(Ct)).ShouldBe(0);
            (await db.Milestones.CountAsync(Ct)).ShouldBe(0);
            (await db.Comments.CountAsync(Ct)).ShouldBe(0);
            (await db.Attachments.CountAsync(Ct)).ShouldBe(0);
            (await db.ImportedItems.CountAsync(Ct)).ShouldBe(0);
            (await db.WorkspaceImports.SingleAsync(i => i.Id == import, Ct)).State.ShouldBe(ImportState.Undone);
        });
        (await Factory.Services.GetRequiredService<ObjectStorage>().SizeAsync($"{_workspace.Id}/{file}", Ct)).ShouldBeNull();
        (await WithImportsAsync(s => s.QueueAsync(import, ImportRunKind.Commit, Ct))).Error!.Code.ShouldBe("import_closed");
    }

    [Fact]
    public async Task Undo_waits_while_records_the_import_did_not_make_point_at_its_issues()
    {
        var import = await ImportedAsync(Source(Item("a", key: "WEB-1"), Item("b", key: "WEB-2")));
        var comment = await ReadAsync<CommentResponse>(await _client.PostAsJsonAsync(Url("issues/WEB-1/comments"), new CommentRequest("Native"), Json.Options, Ct), HttpStatusCode.Created);
        (await _client.PostAsJsonAsync(Url("issues/WEB-1/relations"), new AddRelationRequest(RelationKind.Blocks, "WEB-2"), Json.Options, Ct)).EnsureSuccessStatusCode();

        var refused = await WithImportsAsync(s => s.UndoAsync(import, Ct));

        refused.Value!.Undone.ShouldBeFalse();
        refused.Value.Check.Blockers.Select(b => (b.Kind, b.IssueKey)).ShouldBe([("comment", "WEB-1"), ("relation", "WEB-1")], ignoreOrder: true);
        await WithDbAsync(async db => (await db.Issues.CountAsync(Ct)).ShouldBe(2));

        (await _client.DeleteAsync(Url($"comments/{comment.Id}"), Ct)).EnsureSuccessStatusCode();
        await WithDbAsync(async db => await db.IssueRelations.ExecuteDeleteAsync(Ct));
        (await WithImportsAsync(s => s.UndoAsync(import, Ct))).Value!.Undone.ShouldBeTrue();
    }

    [Fact]
    public async Task Undo_keeps_a_placeholder_someone_merged_history_into()
    {
        var source = Source(Item("a", key: "WEB-1", assignee: "u1"));
        source.Records.Insert(0, new SourcePerson { Id = "u1", Name = "Sofie Larsen" });
        var import = await ImportedAsync(source);
        await ReadAsync<ProjectResponse>(await _client.PostAsJsonAsync(Url("projects"), new CreateProjectRequest("OWN", "Ours"), Json.Options, Ct), HttpStatusCode.Created);
        var placeholder = (await GetAsync<List<MemberResponse>>(_client, Url("members"))).Single(m => m.Status == MemberStatus.Placeholder);
        (await _client.PostAsJsonAsync(Url("issues"), new CreateIssueRequest("OWN", "Theirs", Assignee: placeholder.Id.ToString()), Json.Options, Ct)).EnsureSuccessStatusCode();

        (await WithImportsAsync(s => s.UndoAsync(import, Ct))).Value!.Undone.ShouldBeTrue();

        await WithDbAsync(async db => (await db.Members.CountAsync(m => m.Id == placeholder.Id, Ct)).ShouldBe(1));
    }

    [Fact]
    public async Task Finish_deletes_the_token_and_ends_the_import_but_keeps_its_aliases()
    {
        var import = await ImportedAsync(Source(Item("a", key: "4567")));
        await WithDbAsync(async db =>
        {
            await db.WorkspaceImports.Where(i => i.Id == import).ExecuteUpdateAsync(s => s.SetProperty(i => i.EncryptedToken, "secret").SetProperty(i => i.AutoSync, true), Ct);
        });

        var finished = await WithImportsAsync(s => s.FinishAsync(import, Ct));

        finished.Error.ShouldBeNull();
        await WithDbAsync(async db =>
        {
            var row = await db.WorkspaceImports.SingleAsync(i => i.Id == import, Ct);
            (row.State, row.EncryptedToken, row.AutoSync).ShouldBe((ImportState.Finished, (string?)null, false));
            (await db.IssueAliases.CountAsync(Ct)).ShouldBe(1);
        });
        (await WithImportsAsync(s => s.UndoAsync(import, Ct))).Error!.Code.ShouldBe("import_closed");
    }

    [Fact]
    public async Task People_marked_invite_are_invited_once_the_run_brought_their_history()
    {
        var source = Source(Item("a", key: "WEB-1", assignee: "u1"), Item("b", key: "WEB-2", assignee: "u2"));
        source.Records.InsertRange(0, [new SourcePerson { Id = "u1", Name = "Sofie Larsen", Email = "sofie@acme.example" }, new SourcePerson { Id = "u2", Name = "No Mail" }]);
        var mapping = await MappingAsync(source) with
        {
            People = new Dictionary<string, PersonTarget> { ["u1"] = new(PersonAction.Invite), ["u2"] = new(PersonAction.Invite) },
        };

        var run = await CommitAsync(source, mapping);

        (Counts(run).Invited, Counts(run).NotInvited).ShouldBe((1, 1));
        var members = await GetAsync<List<MemberResponse>>(_client, Url("members"));
        var sofie = members.Single(m => m.Name == "Sofie Larsen");
        sofie.Status.ShouldBe(MemberStatus.Invited);
        (await IssueAsync("WEB-1")).Assignee!.Id.ShouldBe(sofie.Id);
        members.Single(m => m.Name == "No Mail").Status.ShouldBe(MemberStatus.Placeholder);
    }

    [Fact]
    public async Task A_token_unused_for_ninety_days_is_deleted()
    {
        var stale = await ImportedAsync(Source(Item("a", key: "WEB-1")));
        var fresh = await AddImportAsync(Source(Item("b", key: "OTH-1")), await MappingAsync(Source(Item("b", key: "OTH-1"))));
        var now = DateTimeOffset.UtcNow;
        await WithDbAsync(async db =>
        {
            await db.WorkspaceImports.Where(i => i.Id == stale).ExecuteUpdateAsync(s => s
                .SetProperty(i => i.EncryptedToken, "old").SetProperty(i => i.AutoSync, true).SetProperty(i => i.LastRunAt, now - ImportJob.TokenKeptFor - TimeSpan.FromDays(1)), Ct);
            await db.WorkspaceImports.Where(i => i.Id == fresh).ExecuteUpdateAsync(s => s.SetProperty(i => i.EncryptedToken, "new"), Ct);

            (await ImportJob.PurgeTokensAsync(db, now, Ct)).ShouldBe(1);

            var rows = await db.WorkspaceImports.AsNoTracking().ToDictionaryAsync(i => i.Id, Ct);
            (rows[stale].EncryptedToken, rows[stale].AutoSync).ShouldBe(((string?)null, false));
            rows[fresh].EncryptedToken.ShouldBe("new");
        });
    }

    [Theory]
    [InlineData("da-DK", ImportRunState.Succeeded, "Importen fra Jira til Acme er færdig", "12 opgaver")]
    [InlineData("en", ImportRunState.Succeeded, "Your import from Jira into Acme is done", "12 issues")]
    [InlineData("da", ImportRunState.Failed, "Importen fra Jira til Acme stoppede", "Token rejected.")]
    [InlineData("en-GB", ImportRunState.Failed, "Your import from Jira into Acme stopped", "Run the import again")]
    public void The_owner_hears_in_their_language(string locale, ImportRunState state, string subject, string inBody)
    {
        var mail = ImportMail.For("owner@acme.example", locale, "Acme", "Jira", state, 12, "Token rejected.");

        mail.Subject.ShouldBe(subject);
        mail.Body.ShouldContain(inBody);
    }
}
