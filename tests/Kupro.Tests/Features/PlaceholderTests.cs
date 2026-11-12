using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kupro.Api.Data;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Comments;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Meetings;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Kupro.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kupro.Tests.Features;

/// <summary>Placeholder members from imports (task 023, ADR-019, ADR-013): no login, no seat, invite or merge.</summary>
public sealed class PlaceholderTests(PostgresFixture postgres)
    : ApiTestBase(postgres, new Dictionary<string, string?> { ["Members:GuestsPerSeat"] = "1" })
{
    private HttpClient _owner = null!;
    private WorkspaceSummary _workspace = null!;
    private Guid _ownerId;
    private string Ws => $"/api/v1/workspaces/{_workspace.Slug}";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        (_owner, _workspace, _) = await NewWorkspaceAsync();
        _ownerId = (await MembersAsync()).Single().Id;
        await ReadAsync<ProjectResponse>(await _owner.PostAsJsonAsync($"{Ws}/projects", new CreateProjectRequest("KUP", "Kupro"), Json.Options, Ct),
            HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_placeholder_takes_no_seat_no_guest_place_and_cannot_sign_in()
    {
        var user = TestUser.New("Mona");
        var id = await AddPlaceholderAsync("Mona Madsen", user.Email);

        var listed = (await MembersAsync()).Single(m => m.Id == id);
        listed.Status.ShouldBe(MemberStatus.Placeholder);
        listed.Name.ShouldBe("Mona Madsen");
        listed.Email.ShouldBe(user.Email);

        var counts = await GetAsync<WorkspaceResponse>(_owner, Ws);
        counts.Seats.ShouldBe(1);
        counts.Guests.ShouldBe(0);

        // The person behind the placeholder, with the same email, gets neither access nor an invite.
        using var mona = Factory.CreateUserClient(user);
        (await mona.GetAsync(Ws, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync<List<InviteResponse>>(mona, "/api/v1/invites")).ShouldBeEmpty();
        (await GetAsync<List<WorkspaceSummary>>(mona, "/api/v1/workspaces")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_placeholder_can_be_assigned_and_shows_its_name()
    {
        var id = await AddPlaceholderAsync("Mona Madsen");

        var issue = await NewIssueAsync(id);

        issue.Assignee.ShouldNotBeNull().Name.ShouldBe("Mona Madsen");
        (await GetAsync<IssueDetail>(_owner, $"{Ws}/issues/{issue.Key}")).Assignee!.Id.ShouldBe(id);
    }

    [Fact]
    public async Task Role_changes_and_deactivation_are_refused()
    {
        var id = await AddPlaceholderAsync("Mona Madsen");

        await ShouldBeProblemAsync(await _owner.PatchAsJsonAsync($"{Ws}/members/{id}", new UpdateMemberRequest(WorkspaceRole.Guest), Json.Options, Ct),
            HttpStatusCode.Conflict, "member_is_placeholder");
        await ShouldBeProblemAsync(await _owner.PostAsync($"{Ws}/members/{id}/deactivate", null, Ct),
            HttpStatusCode.Conflict, "member_is_placeholder");
    }

    [Fact]
    public async Task Inviting_a_placeholder_keeps_the_same_member_and_its_history()
    {
        var user = TestUser.New("Mona Who Signed Up");
        var id = await AddPlaceholderAsync("Mona Madsen", user.Email.ToUpperInvariant());
        var issue = await NewIssueAsync(id);

        var invited = await ReadAsync<MemberResponse>(await _owner.PostAsJsonAsync($"{Ws}/members/{id}/invite",
            new InvitePlaceholderRequest(WorkspaceRole.Member), Json.Options, Ct));
        invited.Id.ShouldBe(id);
        invited.Status.ShouldBe(MemberStatus.Invited);
        invited.Name.ShouldBe("Mona Madsen");
        invited.Email.ShouldBe(user.Email, "the source's email is the default address");
        (await GetAsync<IssueDetail>(_owner, $"{Ws}/issues/{issue.Key}")).Assignee!.Name.ShouldBe("Mona Madsen");

        using var mona = Factory.CreateUserClient(user);
        await ReadAsync<WorkspaceSummary>(await mona.PostAsync($"/api/v1/invites/{id}/accept", null, Ct));

        var joined = (await MembersAsync()).Single(m => m.Id == id);
        joined.Status.ShouldBe(MemberStatus.Active);
        joined.Name.ShouldBe("Mona Who Signed Up", "the user's own name takes over");
        (await GetAsync<IssueDetail>(mona, $"{Ws}/issues/{issue.Key}")).Assignee!.Name.ShouldBe("Mona Who Signed Up");
        (await GetAsync<WorkspaceResponse>(_owner, Ws)).Seats.ShouldBe(2);
    }

    [Fact]
    public async Task Inviting_a_placeholder_needs_a_free_address_and_room_under_the_guest_cap()
    {
        var noEmail = await AddPlaceholderAsync("No Email");
        await ShouldBeProblemAsync(await _owner.PostAsJsonAsync($"{Ws}/members/{noEmail}/invite",
            new InvitePlaceholderRequest(WorkspaceRole.Member), Json.Options, Ct), HttpStatusCode.BadRequest, "email_required");

        var taken = (await MembersAsync()).Single(m => m.Id == _ownerId).Email;
        await ShouldBeProblemAsync(await _owner.PostAsJsonAsync($"{Ws}/members/{noEmail}/invite",
            new InvitePlaceholderRequest(WorkspaceRole.Member, taken), Json.Options, Ct), HttpStatusCode.Conflict, "already_member");

        await ReadAsync<MemberResponse>(await _owner.PostAsJsonAsync($"{Ws}/members",
            new InviteMemberRequest(TestUser.New().Email, WorkspaceRole.Guest), Json.Options, Ct), HttpStatusCode.Created);
        await ShouldBeProblemAsync(await _owner.PostAsJsonAsync($"{Ws}/members/{noEmail}/invite",
            new InvitePlaceholderRequest(WorkspaceRole.Guest, TestUser.New().Email), Json.Options, Ct), HttpStatusCode.Conflict, "guest_limit_reached");
        (await MembersAsync()).Single(m => m.Id == noEmail).Status.ShouldBe(MemberStatus.Placeholder, "a refused invite changes nothing");

        await ShouldBeProblemAsync(await _owner.PostAsJsonAsync($"{Ws}/members/{_ownerId}/invite",
            new InvitePlaceholderRequest(WorkspaceRole.Member, TestUser.New().Email), Json.Options, Ct), HttpStatusCode.NotFound, "member_not_found");
    }

    [Fact]
    public async Task Merging_moves_every_reference_and_deletes_the_placeholder()
    {
        var placeholder = await AddPlaceholderAsync("Mona Madsen");
        var reassigned = await NewIssueAsync(placeholder);
        await ReadAsync<IssueDetail>(await _owner.PatchAsJsonAsync($"{Ws}/issues/{reassigned.Key}",
            new UpdateIssueRequest(Assignee: _ownerId.ToString()), Json.Options, Ct));
        var assigned = await NewIssueAsync(placeholder);
        await SeedHistoryAsync(placeholder, assigned.Id);

        (await _owner.PostAsJsonAsync($"{Ws}/members/{placeholder}/merge", new MergePlaceholderRequest(_ownerId), Json.Options, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await MembersAsync()).ShouldNotContain(m => m.Id == placeholder);
        (await GetAsync<IssueDetail>(_owner, $"{Ws}/issues/{assigned.Key}")).Assignee!.Id.ShouldBe(_ownerId);

        var feed = await GetAsync<List<FeedItem>>(_owner, $"{Ws}/issues/{assigned.Key}/activity");
        feed.Single(f => f.Kind == FeedItemKind.Comment).Actor.Id.ShouldBe(_ownerId);
        feed.Single(f => f.Kind == FeedItemKind.Comment).Actor.Name.ShouldNotBe("Mona Madsen");
        var reassignment = (await GetAsync<List<FeedItem>>(_owner, $"{Ws}/issues/{reassigned.Key}/activity"))
            .Single(f => f.Kind == FeedItemKind.Updated).Changes.Single(c => c.Field == "assignee");
        reassignment.From.ShouldBe(reassignment.To, "a past assignee in the feed still has a name after the merge");

        await using var scope = Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<WorkspaceContext>().SetForSystem(_workspace.Id);
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(Ct);
        var import = await db.WorkspaceImports.SingleAsync(Ct);
        import.CreatedById.ShouldBe(_ownerId);
        import.Mapping.ShouldNotBeNull().ShouldContain(_ownerId.ToString());
        import.Mapping.ShouldNotContain(placeholder.ToString());
        (await db.ImportedItems.SingleAsync(Ct)).EntityId.ShouldBe(_ownerId);
        (await db.TranscriptSegments.SingleAsync(Ct)).SpeakerId.ShouldBe(_ownerId);
        (await db.MeetingProposals.SingleAsync(Ct)).Value.ShouldBe(_ownerId.ToString());
        (await db.MeetingProposals.SingleAsync(Ct)).Before.ShouldBe(_ownerId.ToString());
        (await db.Issues.AllAsync(i => i.CreatedById == _ownerId, Ct)).ShouldBeTrue();

        // The database itself: no row anywhere still points at the placeholder.
        foreach (var (table, column) in MemberMerge.ForeignKeys(db.Model))
        {
            var count = "SELECT count(*)::int AS \"Value\" FROM \"" + table + "\" WHERE \"" + column + "\" = {0}";
            (await db.Database.SqlQueryRaw<int>(count, placeholder).SingleAsync(Ct)).ShouldBe(0, $"{table}.{column}");
        }
    }

    [Fact]
    public async Task Merge_refuses_a_bad_target_a_non_placeholder_and_other_workspaces()
    {
        var placeholder = await AddPlaceholderAsync("Mona Madsen");
        var other = await AddPlaceholderAsync("Mads Mona");
        var (theirs, theirWorkspace, _) = await NewWorkspaceAsync("Theirs");
        var theirMember = (await GetAsync<List<MemberResponse>>(theirs, $"/api/v1/workspaces/{theirWorkspace.Slug}/members")).Single().Id;

        var pending = await ReadAsync<MemberResponse>(await _owner.PostAsJsonAsync($"{Ws}/members",
            new InviteMemberRequest(TestUser.New().Email, WorkspaceRole.Member), Json.Options, Ct), HttpStatusCode.Created);
        foreach (var target in new[] { placeholder, pending.Id, theirMember, Guid.NewGuid() })
        {
            await ShouldBeProblemAsync(await _owner.PostAsJsonAsync($"{Ws}/members/{placeholder}/merge", new MergePlaceholderRequest(target), Json.Options, Ct),
                HttpStatusCode.BadRequest, "invalid_merge_target");
        }

        await ShouldBeProblemAsync(await _owner.PostAsJsonAsync($"{Ws}/members/{_ownerId}/merge", new MergePlaceholderRequest(other), Json.Options, Ct),
            HttpStatusCode.NotFound, "member_not_found");
        await ShouldBeProblemAsync(await theirs.PostAsJsonAsync($"/api/v1/workspaces/{theirWorkspace.Slug}/members/{placeholder}/merge",
            new MergePlaceholderRequest(theirMember), Json.Options, Ct), HttpStatusCode.NotFound, "member_not_found");

        (await _owner.PostAsJsonAsync($"{Ws}/members/{placeholder}/merge", new MergePlaceholderRequest(other), Json.Options, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent, "placeholders can merge into each other");
    }

    [Fact]
    public async Task Only_owners_invite_or_merge_placeholders()
    {
        var placeholder = await AddPlaceholderAsync("Mona Madsen");
        var user = TestUser.New("Member");
        var invited = await ReadAsync<MemberResponse>(await _owner.PostAsJsonAsync($"{Ws}/members",
            new InviteMemberRequest(user.Email, WorkspaceRole.Member), Json.Options, Ct), HttpStatusCode.Created);
        using var member = Factory.CreateUserClient(user);
        await ReadAsync<WorkspaceSummary>(await member.PostAsync($"/api/v1/invites/{invited.Id}/accept", null, Ct));

        await ShouldBeProblemAsync(await member.PostAsJsonAsync($"{Ws}/members/{placeholder}/merge", new MergePlaceholderRequest(invited.Id), Json.Options, Ct),
            HttpStatusCode.Forbidden, "insufficient_role");
        await ShouldBeProblemAsync(await member.PostAsJsonAsync($"{Ws}/members/{placeholder}/invite",
            new InvitePlaceholderRequest(WorkspaceRole.Member, TestUser.New().Email), Json.Options, Ct), HttpStatusCode.Forbidden, "insufficient_role");
    }

    /// <summary>Foreign keys move by themselves; a member id in a plain column has to be listed, or a merge leaves it behind.</summary>
    [Fact]
    public async Task Every_member_reference_is_covered_by_the_merge()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(Ct);

        var foreignKeys = db.Model.GetEntityTypes().SelectMany(t => t.GetForeignKeys())
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(Member))
            .Select(fk => $"{fk.DeclaringEntityType.ClrType.Name}.{fk.Properties[^1].Name}").ToHashSet();
        foreignKeys.Count.ShouldBe(MemberMerge.ForeignKeys(db.Model).Count());
        foreignKeys.ShouldContain($"{nameof(Issue)}.{nameof(Issue.AssigneeId)}");
        foreignKeys.ShouldContain($"{nameof(ActivityEntry)}.{nameof(ActivityEntry.ActorId)}");
        foreignKeys.ShouldContain($"{nameof(WorkspaceImport)}.{nameof(WorkspaceImport.CreatedById)}");

        var looksLikeAMember = new Regex("(Actor|Assignee|Author|Speaker|Agent|Member|Owner|User|.+By)Id$");
        var uncovered = db.Model.GetEntityTypes()
            .Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType))
            .SelectMany(t => t.GetProperties().Select(p => (Name: $"{t.ClrType.Name}.{p.Name}", p.Name, p.ClrType)))
            .Where(p => (p.ClrType == typeof(Guid) || p.ClrType == typeof(Guid?)) && looksLikeAMember.IsMatch(p.Name))
            .Select(p => p.Item1)
            .Where(name => !foreignKeys.Contains(name) && !MemberMerge.ColumnsWithoutForeignKey.Contains(name) && name != $"{nameof(Member)}.{nameof(Member.UserId)}")
            .ToList();

        uncovered.ShouldBeEmpty("a member reference needs a foreign key, or a line in MemberMerge");
    }

    private async Task<List<MemberResponse>> MembersAsync() => await GetAsync<List<MemberResponse>>(_owner, $"{Ws}/members");

    private async Task<IssueDetail> NewIssueAsync(Guid assignee) =>
        await ReadAsync<IssueDetail>(await _owner.PostAsJsonAsync($"{Ws}/issues",
            new CreateIssueRequest("KUP", "From the old tracker", Assignee: assignee.ToString()), Json.Options, Ct), HttpStatusCode.Created);

    private async Task<Guid> AddPlaceholderAsync(string name, string? email = null)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<WorkspaceContext>().SetForSystem(_workspace.Id);
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(Ct);
        var placeholder = scope.ServiceProvider.GetRequiredService<WorkspaceService>().AddPlaceholder(db, name, email);
        await db.SaveChangesAsync(Ct);
        return placeholder.Id;
    }

    /// <summary>What an import would have written for this person, in every place that holds a member id.</summary>
    private async Task SeedHistoryAsync(Guid placeholder, Guid issueId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<WorkspaceContext>().SetForSystem(_workspace.Id);
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(Ct);
        var now = DateTimeOffset.UtcNow;
        var ws = _workspace.Id;

        db.Comments.Add(new Comment { Id = Guid.CreateVersion7(), WorkspaceId = ws, IssueId = issueId, AuthorId = placeholder, Via = Surface.Api, Body = "Old comment", CreatedAt = now });
        db.Activity.Add(new ActivityEntry { Id = Guid.CreateVersion7(), WorkspaceId = ws, IssueId = issueId, ActorId = placeholder, Via = Surface.Api, Kind = "updated", Data = "{}", CreatedAt = now });

        var project = await db.Projects.Select(p => p.Id).SingleAsync(Ct);
        var meeting = new MeetingSession { Id = Guid.CreateVersion7(), WorkspaceId = ws, ProjectId = project, Language = "en", StartedById = _ownerId, AgentId = _ownerId, StartedAt = now };
        db.Meetings.Add(meeting);
        db.TranscriptSegments.Add(new TranscriptSegment { Id = Guid.CreateVersion7(), WorkspaceId = ws, SessionId = meeting.Id, SpeakerId = placeholder, Text = "Hello", CreatedAt = now });
        db.MeetingProposals.Add(new MeetingProposal
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = ws,
            SessionId = meeting.Id,
            IssueId = issueId,
            Operation = ProposalOperation.Assignee,
            Value = placeholder.ToString(),
            Before = placeholder.ToString(),
            ReviewedById = placeholder,
            CreatedAt = now,
        });

        var import = new WorkspaceImport
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = ws,
            SourceKind = "jira",
            CreatedById = placeholder,
            CreatedAt = now,
            Mapping = JsonSerializer.Serialize(new { people = new { mona = new { member = placeholder } } }),
        };
        db.WorkspaceImports.Add(import);
        db.ImportedItems.Add(new ImportedItem
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = ws,
            ImportId = import.Id,
            SourceKind = "person",
            SourceId = "mona",
            EntityKind = ImportedEntityKind.Member,
            EntityId = placeholder,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
    }
}
