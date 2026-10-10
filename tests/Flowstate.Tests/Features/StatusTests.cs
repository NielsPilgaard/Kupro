using System.Net;
using System.Net.Http.Json;
using Flowstate.Api.Features.Status;
using Flowstate.Tests.Infrastructure;

namespace Flowstate.Tests.Features;

public sealed class StatusTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly FlowstateApiFactory _factory = new(postgres);

    [Fact]
    public async Task Health_is_healthy_when_database_is_reachable()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Status_returns_service_name()
    {
        using var client = _factory.CreateClient();

        var status = await client.GetFromJsonAsync<StatusResponse>(
            "/api/v1/status", TestContext.Current.CancellationToken);

        status.ShouldNotBeNull().Service.ShouldBe("flowstate");
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}
