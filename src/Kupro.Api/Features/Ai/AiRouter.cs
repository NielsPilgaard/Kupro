using Kupro.Api.Data;
using Kupro.Api.Features.Meetings;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kupro.Api.Features.Ai;

/// <summary>Where one model call goes, and who pays for it.</summary>
/// <param name="ConnectionId">The workspace connection; null for Kupro's own route.</param>
/// <param name="Rejected">The workspace's connection was refused by its vendor: the feature degrades and never borrows Kupro's key.</param>
public sealed record ResolvedAiRoute(ModelRoute Route, AiPaidBy PaidBy, Guid? ConnectionId, bool Rejected)
{
    public bool OwnKey => PaidBy == AiPaidBy.Workspace;
}

/// <summary>
/// Picks the route for a capability (ADR-022 decision 3): the workspace's route for the language, then its route for
/// every language, then Kupro's server route (ADR-015 decision 4), or nothing. Routes are cached per workspace; the key is
/// decrypted only when a call is made.
/// </summary>
public sealed class AiRouter(WorkspaceCache cache, AiKeys keys, IOptions<MeetingAgentOptions> options)
{
    private const string CacheKey = "ai-routes";

    /// <summary>One route joined to its connection, as cached. The key is still encrypted.</summary>
    public sealed record RouteRow(
        AiCapability Capability, string Language, Guid ConnectionId, string ProviderType, string? Endpoint, string? ProjectId,
        string EncryptedKey, AiConnectionStatus Status, string? Model);

    public async Task<ResolvedAiRoute?> ResolveAsync(AiCapability capability, string language, CancellationToken ct)
    {
        var rows = await RowsAsync(ct);
        var row = rows.FirstOrDefault(r => r.Capability == capability && r.Language == language)
                  ?? rows.FirstOrDefault(r => r.Capability == capability && r.Language.Length == 0);
        if (row is not null && AiProviders.Find(row.ProviderType) is { } provider)
        {
            var chat = capability != AiCapability.Transcription;
            return new ResolvedAiRoute(new ModelRoute
            {
                Endpoint = provider.BaseUrl(row.Endpoint, row.ProjectId),
                Model = row.Model ?? provider.DefaultModel(capability),
                ApiKey = keys.Unprotect(row.ConnectionId, row.EncryptedKey),
                // The effort belongs to the tested default; another model may not take the parameter.
                ReasoningEffort = chat && row.Model is null ? provider.ReasoningEffort : null,
                EuroPerAudioMinute = provider.EuroPerAudioMinute,
                EuroPerMillionInputTokens = provider.EuroPerMillionInputTokens,
                EuroPerMillionCachedInputTokens = provider.EuroPerMillionCachedInputTokens,
                EuroPerMillionOutputTokens = provider.EuroPerMillionOutputTokens,
                PublicOnly = true,
            }, AiPaidBy.Workspace, row.ConnectionId, row.Status == AiConnectionStatus.Rejected);
        }

        var server = capability == AiCapability.Transcription
            ? options.Value.Transcription.For(language)
            : options.Value.Extraction.For(language);
        return server.IsConfigured ? new ResolvedAiRoute(server, AiPaidBy.Kupro, null, false) : null;
    }

    /// <summary>True when the capability has a working route for the language, own or Kupro's.</summary>
    public async Task<bool> AvailableAsync(AiCapability capability, string language, CancellationToken ct) =>
        await ResolveAsync(capability, language, ct) is { Rejected: false };

    public ValueTask<IReadOnlyList<RouteRow>> RowsAsync(CancellationToken ct) =>
        cache.GetOrSetAsync<int, IReadOnlyList<RouteRow>>(CacheKey, 0, static async (services, _, token) =>
        {
            await using var db = await services.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(token);
            return await db.AiRoutes.AsNoTracking()
                .Join(db.AiConnections, r => r.ConnectionId, c => c.Id, (r, c) => new RouteRow(
                    r.Capability, r.Language, c.Id, c.ProviderType, c.Endpoint, c.ProjectId, c.EncryptedKey, c.Status, r.Model))
                .ToListAsync(token);
        }, static rows => 1 + rows.Count, ct);
}
