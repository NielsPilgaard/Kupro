namespace Flowstate.Tests.Infrastructure;

/// <summary>Base for HTTP-level tests: one API instance per test class, migrated database.</summary>
public abstract class ApiTestBase(PostgresFixture postgres) : IAsyncLifetime
{
    protected FlowstateApiFactory Factory { get; } = new(postgres);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public virtual async ValueTask InitializeAsync() => await Factory.InitializeDatabaseAsync();

    public virtual async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
