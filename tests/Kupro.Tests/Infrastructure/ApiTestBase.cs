using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kupro.Api.Features.Tokens;
using Kupro.Api.Features.Workspaces;

namespace Kupro.Tests.Infrastructure;

/// <summary>Base for HTTP-level tests: one API instance per test class, migrated database.</summary>
public abstract class ApiTestBase(PostgresFixture postgres, IReadOnlyDictionary<string, string?>? settings = null) : IAsyncLifetime
{
    protected KuproApiFactory Factory { get; } = new(postgres, settings);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public virtual async ValueTask InitializeAsync() => await Factory.InitializeDatabaseAsync();

    public virtual async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>A fresh user with a fresh workspace they own. Slugs are unique per test run.</summary>
    protected async Task<(HttpClient Client, WorkspaceSummary Workspace, TestUser User)> NewWorkspaceAsync(string name = "Acme")
    {
        var user = TestUser.New();
        var client = Factory.CreateUserClient(user);
        var slug = "w-" + Guid.NewGuid().ToString("N")[..12];
        var response = await client.PostAsJsonAsync("/api/v1/workspaces", new CreateWorkspaceRequest(name, slug), Json.Options, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (client, (await response.Content.ReadFromJsonAsync<WorkspaceSummary>(Json.Options, Ct))!, user);
    }

    protected static async Task<CreatedApiTokenResponse> NewTokenAsync(HttpClient client, CreateApiTokenRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/v1/tokens", request, Json.Options, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CreatedApiTokenResponse>(Json.Options, Ct))!;
    }

    protected static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Json.Options, Ct))!;
    }

    protected static async Task<T> GetAsync<T>(HttpClient client, string url) => await ReadAsync<T>(await client.GetAsync(url, Ct));

    protected static async Task ShouldBeProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("code").GetString().ShouldBe(code);
    }
}
