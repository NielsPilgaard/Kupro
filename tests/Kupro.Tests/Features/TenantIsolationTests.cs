using System.Net;
using System.Net.Http.Json;
using Kupro.Api.Data;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Plan;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Sprints;
using Kupro.Api.Features.Tokens;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Kupro.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Kupro.Tests.Features;

/// <summary>
/// ADR-009: ids and keys from another workspace behave as if they don't exist, whichever way they come in, and the
/// database refuses cross-workspace references even when the application doesn't.
/// </summary>
public sealed class TenantIsolationTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private HttpClient _alice = null!;
    private WorkspaceSummary _mine = null!;
    private WorkspaceSummary _theirs = null!;
    private SprintResponse _theirSprint = null!;
    private MilestoneResponse _theirMilestone = null!;
    private IssueDetail _theirIssue = null!;
    private Guid _theirMember;

    private string Mine => $"/api/v1/workspaces/{_mine.Slug}";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        (_alice, _mine, _) = await NewWorkspaceAsync("Mine");
        await SeedAsync(_alice, _mine.Slug, "KUP");

        var (bob, theirs, _) = await NewWorkspaceAsync("Theirs");
        _theirs = theirs;
        (_theirIssue, _theirSprint) = await SeedAsync(bob, theirs.Slug, "OTH");
        _theirMilestone = await ReadAsync<MilestoneResponse>(await bob.PostAsJsonAsync($"/api/v1/workspaces/{theirs.Slug}/milestones",
            new CreateMilestoneRequest("Launch", new DateOnly(2026, 12, 1), "OTH"), Json.Options, Ct), HttpStatusCode.Created);
        await ReadAsync<IssueDetail>(await bob.PostAsJsonAsync($"/api/v1/workspaces/{theirs.Slug}/issues",
            new CreateIssueRequest("OTH", "Labelled", Labels: ["secret"]), Json.Options, Ct), HttpStatusCode.Created);
        _theirMember = (await GetAsync<List<MemberResponse>>(bob, $"/api/v1/workspaces/{theirs.Slug}/members")).Single().Id;
    }

    [Fact]
    public async Task Every_entity_with_a_workspace_id_gets_the_tenant_filter()
    {
        var unfiltered = (await ModelAsync()).GetEntityTypes()
            .Where(t => t.FindProperty(nameof(ITenantOwned.WorkspaceId)) is not null && !IsTenantOwned(t.ClrType))
            .Select(t => t.ClrType.Name);

        unfiltered.ShouldBe([nameof(ApiToken)], "a token belongs to its user; its WorkspaceId only narrows what it can reach");
    }

    [Fact]
    public async Task Every_reference_between_tenant_owned_entities_includes_the_workspace()
    {
        var unscoped = (await ModelAsync()).GetEntityTypes().SelectMany(t => t.GetForeignKeys())
            .Where(fk => IsTenantOwned(fk.DeclaringEntityType.ClrType) && IsTenantOwned(fk.PrincipalEntityType.ClrType))
            .Where(fk => fk.Properties[0].Name != nameof(ITenantOwned.WorkspaceId)
                         || fk.PrincipalKey.Properties[0].Name != nameof(ITenantOwned.WorkspaceId))
            .Select(fk => $"{fk.DeclaringEntityType.ClrType.Name}.{fk.Properties[^1].Name}");

        unscoped.ShouldBeEmpty("use HasWorkspaceForeignKey so the database rejects cross-workspace references");
    }

    [Fact]
    public async Task Database_rejects_references_into_another_workspace()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<WorkspaceContext>().SetForSystem(_mine.Id);
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>();

        foreach (var smuggle in new Action<Issue>[] { i => i.SprintId = _theirSprint.Id, i => i.ParentId = _theirIssue.Id, i => i.AssigneeId = _theirMember })
        {
            await using var db = await factory.CreateDbContextAsync(Ct);
            var issue = await db.Issues.SingleAsync(i => i.Key == "KUP-1", Ct);
            smuggle(issue);

            var error = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
            error.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        }
    }

    [Fact]
    public async Task Ids_from_another_workspace_are_not_found()
    {
        var sprint = $"{Mine}/sprints/{_theirSprint.Id}";
        (await _alice.GetAsync(sprint, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _alice.PatchAsJsonAsync(sprint, new UpdateSprintRequest(Name: "Mine now"), Json.Options, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _alice.PostAsync($"{sprint}/start", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _alice.PostAsJsonAsync($"{sprint}/close", new CloseSprintRequest(UnfinishedDestination.Backlog), Json.Options, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var milestone = $"{Mine}/milestones/{_theirMilestone.Id}";
        (await _alice.PatchAsJsonAsync(milestone, new UpdateMilestoneRequest(Name: "Mine now"), Json.Options, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _alice.DeleteAsync(milestone, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var member = $"{Mine}/members/{_theirMember}";
        (await _alice.PatchAsJsonAsync(member, new UpdateMemberRequest(Role: WorkspaceRole.Guest), Json.Options, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _alice.PostAsync($"{member}/deactivate", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await AssertTheirsUntouchedAsync();
    }

    [Fact]
    public async Task Keys_and_names_from_another_workspace_are_not_found()
    {
        await ShouldBeProblemAsync(await _alice.GetAsync($"{Mine}/projects/OTH/capacity", Ct), HttpStatusCode.NotFound, "project_not_found");
        await ShouldBeProblemAsync(await _alice.PutAsJsonAsync($"{Mine}/projects/OTH/capacity",
            new SetCapacityRequest([]), Json.Options, Ct), HttpStatusCode.NotFound, "project_not_found");
        await ShouldBeProblemAsync(await _alice.PatchAsJsonAsync($"{Mine}/labels/secret",
            new UpdateLabelRequest(Name: "mine"), Json.Options, Ct), HttpStatusCode.NotFound, "label_not_found");
        await ShouldBeProblemAsync(await _alice.PostAsJsonAsync($"{Mine}/issues",
            new CreateIssueRequest("OTH", "Into their project"), Json.Options, Ct), HttpStatusCode.BadRequest, "project_not_found");
    }

    [Fact]
    public async Task Issues_cannot_point_into_another_workspace()
    {
        var issue = $"{Mine}/issues/KUP-1";
        await ShouldBeProblemAsync(await _alice.PatchAsJsonAsync(issue, new UpdateIssueRequest(Sprint: _theirSprint.Id.ToString()), Json.Options, Ct),
            HttpStatusCode.BadRequest, "invalid_sprint");
        await ShouldBeProblemAsync(await _alice.PatchAsJsonAsync(issue, new UpdateIssueRequest(Parent: _theirIssue.Key), Json.Options, Ct),
            HttpStatusCode.BadRequest, "invalid_parent");
        await ShouldBeProblemAsync(await _alice.PatchAsJsonAsync(issue, new UpdateIssueRequest(Assignee: _theirMember.ToString()), Json.Options, Ct),
            HttpStatusCode.BadRequest, "assignee_not_found");
        await ShouldBeProblemAsync(await _alice.PostAsJsonAsync($"{issue}/relations", new AddRelationRequest(RelationKind.Blocks, _theirIssue.Key),
            Json.Options, Ct), HttpStatusCode.NotFound, "issue_not_found");

        var detail = await GetAsync<IssueDetail>(_alice, issue);
        detail.Parent.ShouldBeNull();
        detail.Assignee.ShouldBeNull();
    }

    private async Task AssertTheirsUntouchedAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<WorkspaceContext>().SetForSystem(_theirs.Id);
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(Ct);
        (await db.Sprints.SingleAsync(s => s.Id == _theirSprint.Id, Ct)).Name.ShouldBe("S1");
        (await db.Milestones.SingleAsync(m => m.Id == _theirMilestone.Id, Ct)).Name.ShouldBe("Launch");
        (await db.Members.SingleAsync(m => m.Id == _theirMember, Ct)).Role.ShouldBe(WorkspaceRole.Owner);
    }

    private static async Task<(IssueDetail Issue, SprintResponse Sprint)> SeedAsync(HttpClient client, string slug, string key)
    {
        var ws = $"/api/v1/workspaces/{slug}";
        await ReadAsync<ProjectResponse>(await client.PostAsJsonAsync($"{ws}/projects", new CreateProjectRequest(key, key), Json.Options, Ct),
            HttpStatusCode.Created);
        var issue = await ReadAsync<IssueDetail>(await client.PostAsJsonAsync($"{ws}/issues", new CreateIssueRequest(key, "First"), Json.Options, Ct),
            HttpStatusCode.Created);
        var sprint = await ReadAsync<SprintResponse>(await client.PostAsJsonAsync($"{ws}/sprints",
            new CreateSprintRequest(key, "S1", new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 13)), Json.Options, Ct), HttpStatusCode.Created);
        return (issue, sprint);
    }

    private async Task<IModel> ModelAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(Ct);
        return db.Model;
    }

    private static bool IsTenantOwned(Type type) => typeof(ITenantOwned).IsAssignableFrom(type);
}
