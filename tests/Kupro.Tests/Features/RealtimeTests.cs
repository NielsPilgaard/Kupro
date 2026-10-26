using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Comments;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Realtime;
using Kupro.Api.Features.Tokens;
using Kupro.Api.Features.Workspaces;
using Kupro.Tests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Kupro.Tests.Features;

/// <summary>Task 012: changes reach connected members of the same workspace, and nobody else.</summary>
public sealed class RealtimeTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(1);
    private HttpClient _client = null!;
    private WorkspaceSummary _workspace = null!;
    private TestUser _user = null!;
    private readonly List<HubConnection> _connections = [];
    private string Ws => $"/api/v1/workspaces/{_workspace.Slug}";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        (_client, _workspace, _user) = await NewWorkspaceAsync();
        await ReadAsync<ProjectResponse>(await _client.PostAsJsonAsync($"{Ws}/projects", new CreateProjectRequest("KUP", "Kupro"), Json.Options, Ct),
            HttpStatusCode.Created);
        await ReadAsync<IssueDetail>(await _client.PostAsJsonAsync($"{Ws}/issues", new CreateIssueRequest("KUP", "Live"), Json.Options, Ct),
            HttpStatusCode.Created);
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    [Fact]
    public async Task A_CLI_change_reaches_an_open_browser_within_a_second()
    {
        var browser = await ConnectAsync(KuproApiFactory.CreateJwt(_user));
        var received = Expect<IssuesEvent>(browser, "Issues");
        (await browser.InvokeAsync<bool>("Join", _workspace.Slug, Ct)).ShouldBeTrue();

        var agent = Factory.CreateBearerClient((await NewTokenAsync(_client, new("cli", ApiTokenKind.Agent, "cli-bot", null, _workspace.Slug))).Secret);
        agent.DefaultRequestHeaders.Add("X-Kupro-Client", "cli");
        (await agent.PatchAsJsonAsync($"{Ws}/issues/KUP-1", new UpdateIssueRequest(State: IssueState.InProgress), Json.Options, Ct))
            .EnsureSuccessStatusCode();

        var change = await received.WaitAsync(Wait, Ct);
        change.Items.ShouldHaveSingleItem().State.ShouldBe(IssueState.InProgress);
        change.Created.ShouldBeFalse();
        change.Via.ShouldBe(Surface.Cli);
    }

    [Fact]
    public async Task Comments_and_sprints_send_refetch_hints()
    {
        var browser = await ConnectAsync(KuproApiFactory.CreateJwt(_user));
        var changes = new ConcurrentQueue<ChangedEvent>();
        browser.On<ChangedEvent>("Changed", changes.Enqueue);
        await browser.InvokeAsync<bool>("Join", _workspace.Slug, Ct);

        await _client.PostAsJsonAsync($"{Ws}/issues/KUP-1/comments", new CommentRequest("hi"), Json.Options, Ct);
        await _client.PostAsJsonAsync($"{Ws}/sprints",
            new Api.Features.Sprints.CreateSprintRequest("KUP", "S1", new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 16)), Json.Options, Ct);

        await WaitUntilAsync(() => changes.Count >= 2);
        changes.ShouldContain(c => c.Topic == ChangeTopic.Issue && c.IssueKey == "KUP-1");
        changes.ShouldContain(c => c.Topic == ChangeTopic.Sprints);
    }

    [Fact]
    public async Task Other_workspaces_hear_nothing()
    {
        var (_, otherWorkspace, outsider) = await NewWorkspaceAsync("Other");
        var connection = await ConnectAsync(KuproApiFactory.CreateJwt(outsider));
        var heard = false;
        connection.On<IssuesEvent>("Issues", _ => heard = true);

        (await connection.InvokeAsync<bool>("Join", _workspace.Slug, Ct)).ShouldBeFalse();
        (await connection.InvokeAsync<bool>("Join", otherWorkspace.Slug, Ct)).ShouldBeTrue();
        (await _client.PatchAsJsonAsync($"{Ws}/issues/KUP-1", new UpdateIssueRequest(Title: "Moved"), Json.Options, Ct)).EnsureSuccessStatusCode();

        await Task.Delay(300, Ct);
        heard.ShouldBeFalse();
    }

    [Fact]
    public async Task Deactivated_members_stop_hearing_without_reconnecting()
    {
        var colleague = TestUser.New("Ida");
        using var ida = Factory.CreateUserClient(colleague);
        var invite = await ReadAsync<MemberResponse>(await _client.PostAsJsonAsync($"{Ws}/members",
            new InviteMemberRequest(colleague.Email, WorkspaceRole.Member), Json.Options, Ct), HttpStatusCode.Created);
        await ReadAsync<WorkspaceSummary>(await ida.PostAsync($"/api/v1/invites/{invite.Id}/accept", null, Ct));
        var connection = await ConnectAsync(KuproApiFactory.CreateJwt(colleague));
        var heard = 0;
        connection.On<IssuesEvent>("Issues", _ => Interlocked.Increment(ref heard));
        (await connection.InvokeAsync<bool>("Join", _workspace.Slug, Ct)).ShouldBeTrue();
        (await _client.PatchAsJsonAsync($"{Ws}/issues/KUP-1", new UpdateIssueRequest(Title: "Heard"), Json.Options, Ct)).EnsureSuccessStatusCode();
        await WaitUntilAsync(() => heard == 1);

        (await _client.PostAsync($"{Ws}/members/{invite.Id}/deactivate", null, Ct)).EnsureSuccessStatusCode();
        (await _client.PatchAsJsonAsync($"{Ws}/issues/KUP-1", new UpdateIssueRequest(Title: "Unheard"), Json.Options, Ct)).EnsureSuccessStatusCode();

        await Task.Delay(300, Ct);
        heard.ShouldBe(1);
        (await connection.InvokeAsync<bool>("Join", _workspace.Slug, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Connecting_needs_a_token()
    {
        var connection = Build(null);
        var error = await Should.ThrowAsync<HttpRequestException>(() => connection.StartAsync(Ct));
        error.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<HubConnection> ConnectAsync(string? token)
    {
        var connection = Build(token);
        await connection.StartAsync(Ct);
        return connection;
    }

    /// <summary>WebSockets with the token in the query string, as browsers send it.</summary>
    private HubConnection Build(string? token)
    {
        var url = new Uri(Factory.Server.BaseAddress, WorkspaceHub.Path + (token is null ? "" : $"?access_token={Uri.EscapeDataString(token)}"));
        var connection = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                options.Transports = HttpTransportType.WebSockets;
                options.WebSocketFactory = async (context, ct) =>
                {
                    var client = Factory.Server.CreateWebSocketClient();
                    return await client.ConnectAsync(context.Uri, ct);
                };
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)))
            .Build();
        _connections.Add(connection);
        return connection;
    }

    private static Task<T> Expect<T>(HubConnection connection, string method)
    {
        var received = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<T>(method, value => received.TrySetResult(value));
        return received.Task;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        condition().ShouldBeTrue();
    }
}
