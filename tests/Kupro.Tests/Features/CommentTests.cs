using System.Net;
using System.Net.Http.Json;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Comments;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Tokens;
using Kupro.Api.Features.Workspaces;
using Kupro.Tests.Infrastructure;

namespace Kupro.Tests.Features;

/// <summary>Task 008: comments and the merged activity feed.</summary>
public sealed class CommentTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private HttpClient _client = null!;
    private WorkspaceSummary _workspace = null!;
    private string Ws => $"/api/v1/workspaces/{_workspace.Slug}";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        (_client, _workspace, _) = await NewWorkspaceAsync();
        await ReadAsync<ProjectResponse>(await _client.PostAsJsonAsync($"{Ws}/projects", new CreateProjectRequest("KUP", "Kupro"), Json.Options, Ct),
            HttpStatusCode.Created);
        await ReadAsync<IssueDetail>(await _client.PostAsJsonAsync($"{Ws}/issues", new CreateIssueRequest("KUP", "Feed me"), Json.Options, Ct),
            HttpStatusCode.Created);
    }

    [Fact]
    public async Task Author_adds_edits_and_deletes_a_comment()
    {
        var comment = await ReadAsync<CommentResponse>(
            await _client.PostAsJsonAsync($"{Ws}/issues/kup-1/comments", new CommentRequest("Looks **good**"), Json.Options, Ct),
            HttpStatusCode.Created);
        comment.IssueKey.ShouldBe("KUP-1");
        comment.Author.Name.ShouldBe("Test User");
        comment.EditedAt.ShouldBeNull();

        var edited = await ReadAsync<CommentResponse>(
            await _client.PatchAsJsonAsync($"{Ws}/comments/{comment.Id}", new CommentRequest("Looks great"), Json.Options, Ct));
        edited.Body.ShouldBe("Looks great");
        edited.EditedAt.ShouldNotBeNull();

        (await _client.DeleteAsync($"{Ws}/comments/{comment.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await FeedAsync()).ShouldNotContain(i => i.Kind == FeedItemKind.Comment);
    }

    [Fact]
    public async Task Only_the_author_can_change_a_comment()
    {
        var agent = Factory.CreateBearerClient((await NewTokenAsync(_client,
            new("bot", ApiTokenKind.Agent, "review-bot", null, _workspace.Slug))).Secret);
        var byAgent = await ReadAsync<CommentResponse>(
            await agent.PostAsJsonAsync($"{Ws}/issues/KUP-1/comments", new CommentRequest("Reviewed by an agent."), Json.Options, Ct),
            HttpStatusCode.Created);
        byAgent.Author.Kind.ShouldBe(MemberKind.Agent);

        await ShouldBeProblemAsync(
            await _client.PatchAsJsonAsync($"{Ws}/comments/{byAgent.Id}", new CommentRequest("hijack"), Json.Options, Ct),
            HttpStatusCode.Forbidden, "not_comment_author");
        await ShouldBeProblemAsync(await _client.DeleteAsync($"{Ws}/comments/{byAgent.Id}", Ct),
            HttpStatusCode.Forbidden, "not_comment_author");
    }

    [Fact]
    public async Task Feed_merges_history_and_comments_with_readable_values()
    {
        (await _client.PatchAsJsonAsync($"{Ws}/issues/KUP-1",
            new UpdateIssueRequest(State: IssueState.InProgress, Assignee: "me", Labels: ["ui"]), Json.Options, Ct)).EnsureSuccessStatusCode();
        await _client.PostAsJsonAsync($"{Ws}/issues/KUP-1/comments", new CommentRequest("On it."), Json.Options, Ct);

        var feed = await FeedAsync();

        feed.Select(i => i.Kind).ShouldBe([FeedItemKind.Created, FeedItemKind.Updated, FeedItemKind.Comment]);
        var changes = feed[1].Changes.ToDictionary(c => c.Field);
        changes["state"].ShouldBe(new FeedChange("state", "backlog", "inProgress"));
        changes["assignee"].ShouldBe(new FeedChange("assignee", null, "Test User"));
        changes["labels"].To.ShouldBe("ui");
        feed[1].Actor.Name.ShouldBe("Test User");
        feed[2].Body.ShouldBe("On it.");
    }

    [Fact]
    public async Task Comments_on_missing_or_foreign_issues_are_not_found()
    {
        await ShouldBeProblemAsync(
            await _client.PostAsJsonAsync($"{Ws}/issues/KUP-99/comments", new CommentRequest("?"), Json.Options, Ct),
            HttpStatusCode.NotFound, "issue_not_found");

        var comment = await ReadAsync<CommentResponse>(
            await _client.PostAsJsonAsync($"{Ws}/issues/KUP-1/comments", new CommentRequest("mine"), Json.Options, Ct),
            HttpStatusCode.Created);
        var (other, otherWorkspace, _) = await NewWorkspaceAsync("Other");
        await ShouldBeProblemAsync(
            await other.PatchAsJsonAsync($"/api/v1/workspaces/{otherWorkspace.Slug}/comments/{comment.Id}", new CommentRequest("x"), Json.Options, Ct),
            HttpStatusCode.NotFound, "comment_not_found");
    }

    [Fact]
    public async Task Attachments_report_unavailable_without_storage()
    {
        await ShouldBeProblemAsync(
            await _client.PostAsJsonAsync($"{Ws}/attachments", new { fileName = "a.png", contentType = "image/png", size = 10 }, Json.Options, Ct),
            HttpStatusCode.ServiceUnavailable, "attachments_unavailable");
    }

    private async Task<List<FeedItem>> FeedAsync() => await GetAsync<List<FeedItem>>(_client, $"{Ws}/issues/KUP-1/activity");
}
