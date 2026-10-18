using Testcontainers.PostgreSql;

namespace Kupro.Tests.Infrastructure;

/// <summary>One Postgres container per test run, shared by all test classes (ADR-002 IMP-002).</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18").Build();

    public string ConnectionString => _container.GetConnectionString();

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
