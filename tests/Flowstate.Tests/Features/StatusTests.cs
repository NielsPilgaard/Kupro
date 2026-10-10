using System.Net;
using System.Net.Http.Json;
using Flowstate.Api.Features.Status;
using Flowstate.Tests.Infrastructure;

namespace Flowstate.Tests.Features;

public sealed class StatusTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task Health_is_healthy_when_database_is_reachable()
    {
        using var client = Factory.CreateClient();

        var response = await client.GetAsync("/health", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Status_is_anonymous_and_returns_service_name()
    {
        using var client = Factory.CreateClient();

        var status = await client.GetFromJsonAsync<StatusResponse>("/api/v1/status", Ct);

        status.ShouldNotBeNull().Service.ShouldBe("flowstate");
    }
}
