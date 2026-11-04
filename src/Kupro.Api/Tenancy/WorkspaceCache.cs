using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using ZiggyCreatures.Caching.Fusion;

namespace Kupro.Api.Tenancy;

/// <summary>
/// Read-through cache for one workspace's data (ADR-010 IMP-003) and the only way to reach <see cref="IFusionCache"/>.
/// It prefixes every key with the workspace id and its generation, so callers can't leak across workspaces, and
/// invalidating moves the workspace to a new generation: a refresh that started before a write stores under a key nobody
/// reads again.
/// <para>
/// Each factory runs in its own scope, pinned to the caller's workspace, without the request's ExecutionContext. That
/// holds on every call, so a factory reaching for request state fails at once instead of only on a rare background
/// refresh. Pass a static factory and everything it needs as <c>state</c>.
/// </para>
/// </summary>
public sealed class WorkspaceCache(
    IFusionCache cache, WorkspaceContext workspace, WorkspaceCacheGenerations generations, IServiceScopeFactory scopeFactory)
{
    /// <param name="key">Unique within the workspace.</param>
    /// <param name="state">What the factory needs.</param>
    /// <param name="factory">Resolves the feature service from the scope it is given.</param>
    /// <param name="sizeInKilobytes">Rough size of a value, counted against <see cref="WorkspaceCacheExtensions.SizeLimitKilobytes"/>.</param>
    /// <param name="ct">The request's token.</param>
    public ValueTask<TValue> GetOrSetAsync<TState, TValue>(
        string key,
        TState state,
        Func<IServiceProvider, TState, CancellationToken, Task<TValue>> factory,
        Func<TValue, long> sizeInKilobytes,
        CancellationToken ct)
    {
        var workspaceId = workspace.RequireWorkspaceId();
        return cache.GetOrSetAsync<TValue>(
            $"{workspaceId}:{generations.Current(workspaceId)}:{key}",
            async (context, token) =>
            {
                var value = await RunDetachedAsync(workspaceId, state, factory, token);
                context.Options.Size = sizeInKilobytes(value);
                return value;
            },
            token: ct);
    }

    /// <summary>Call after commit, before telling anyone about the change. Can't fail, so it can't fail a committed write.</summary>
    public void Invalidate()
    {
        if (workspace.WorkspaceId is { } id)
        {
            generations.Advance(id);
        }
    }

    private Task<TValue> RunDetachedAsync<TState, TValue>(
        Guid workspaceId,
        TState state,
        Func<IServiceProvider, TState, CancellationToken, Task<TValue>> factory,
        CancellationToken ct)
    {
        var parentActivity = Activity.Current;

        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(
                async () =>
                {
                    // Suppressing the flow drops the trace too; keep the factory's queries on the request's.
                    Activity.Current = parentActivity;

                    await using var scope = scopeFactory.CreateAsyncScope();
                    // An unpinned context returns empty rows, which would then be cached.
                    scope.ServiceProvider.GetRequiredService<WorkspaceContext>().SetForSystem(workspaceId);

                    return await factory(scope.ServiceProvider, state, ct);
                },
                ct);
        }
    }
}

/// <summary>
/// Current generation per workspace. In memory like the cache itself, which assumes one API instance: a restart empties
/// both. One entry per workspace that has written since then.
/// </summary>
public sealed class WorkspaceCacheGenerations
{
    private readonly ConcurrentDictionary<Guid, long> _generations = new();

    public long Current(Guid workspaceId) => _generations.GetValueOrDefault(workspaceId);

    public void Advance(Guid workspaceId) => _generations.AddOrUpdate(workspaceId, 1, static (_, generation) => generation + 1);
}

public static class WorkspaceCacheExtensions
{
    /// <summary>64 MB of cached values, roughly: about 250 full plans of a workspace with 1,000 dated epics.</summary>
    public const long SizeLimitKilobytes = 64 * 1024;

    public static IServiceCollection AddWorkspaceCache(this IServiceCollection services)
    {
        services.AddFusionCache()
            // Its own MemoryCache: a size limit requires every entry to have a size, and other users of the shared one don't.
            .WithMemoryCache(new MemoryCache(new MemoryCacheOptions { SizeLimit = SizeLimitKilobytes }))
            .WithDefaultEntryOptions(options =>
            {
                options.Duration = TimeSpan.FromMinutes(5); // A safety net: writes invalidate.
                options.Size = 1;
                options.EagerRefreshThreshold = 0.9f;
                // The soft timeout needs fail-safe, and serves the last value while the factory finishes in the background.
                options.FactorySoftTimeout = TimeSpan.FromSeconds(1);
                options.IsFailSafeEnabled = true;
                options.FailSafeMaxDuration = TimeSpan.FromMinutes(15);
            });
        services.AddSingleton<WorkspaceCacheGenerations>();
        services.AddScoped<WorkspaceCache>();
        return services;
    }
}
