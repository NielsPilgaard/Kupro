using Flowstate.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Flowstate.Tests.Infrastructure;

/// <summary>In-process API wired to the shared Testcontainers Postgres.</summary>
public sealed class FlowstateApiFactory(PostgresFixture postgres) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting($"ConnectionStrings:{DatabaseExtensions.ConnectionName}", postgres.ConnectionString);
    }
}
