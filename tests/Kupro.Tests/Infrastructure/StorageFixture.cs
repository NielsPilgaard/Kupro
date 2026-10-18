using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Kupro.Tests.Infrastructure;

/// <summary>S3-compatible storage (Silo, the MinIO fork, as in dev) for attachment tests; one container per test class.</summary>
public sealed class StorageFixture : IAsyncLifetime
{
    private const int Port = 9000;
    private const string AccessKey = "kupro";
    private const string SecretKey = "kupro-test-secret";

    private readonly IContainer _container = new ContainerBuilder("pgsty/silo:RELEASE.2026-09-16T00-00-00Z")
        .WithCommand("server", "/data")
        .WithEnvironment("MINIO_ROOT_USER", AccessKey)
        .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
        .WithTmpfsMount("/data")
        .WithPortBinding(Port, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(Port).ForPath("/minio/health/live")))
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
