using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Flowstate.Tests.Infrastructure;

/// <summary>S3-compatible storage (versitygw, as in dev) for attachment tests; one container per test class.</summary>
public sealed class StorageFixture : IAsyncLifetime
{
    private const int Port = 7070;
    private const string AccessKey = "flowstate";
    private const string SecretKey = "flowstate-test-secret";

    private readonly IContainer _container = new ContainerBuilder("versity/versitygw:v1.8.0")
        .WithCommand("posix", "/data")
        .WithEnvironment("ROOT_ACCESS_KEY_ID", AccessKey)
        .WithEnvironment("ROOT_SECRET_ACCESS_KEY", SecretKey)
        .WithTmpfsMount("/data")
        .WithPortBinding(Port, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("listening on"))
        .Build();

    public IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Storage:ServiceUrl"] = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(Port)}",
        ["Storage:AccessKey"] = AccessKey,
        ["Storage:SecretKey"] = SecretKey,
        ["Storage:CreateBucket"] = "true",
    };

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
