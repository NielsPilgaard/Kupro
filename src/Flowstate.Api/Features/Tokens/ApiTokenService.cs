using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Flowstate.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Flowstate.Api.Features.Tokens;

/// <param name="Name">Label to recognise the token, e.g. "CI deploy" or "laptop CLI".</param>
/// <param name="Kind"><c>personal</c> acts as you; <c>agent</c> acts on your behalf under the agent's name.</param>
/// <param name="AgentName">Required for agent tokens: lowercase name shown in the activity log, e.g. <c>triage-agent</c>.</param>
/// <param name="ExpiresInDays">Days until expiry (1-365); omit for a token that does not expire.</param>
public sealed record CreateApiTokenRequest(
    [Required, StringLength(100, MinimumLength = 1)] string Name,
    ApiTokenKind Kind,
    [StringLength(64, MinimumLength = 2), RegularExpression("^[a-z0-9][a-z0-9-]*$")] string? AgentName = null,
    [Range(1, 365)] int? ExpiresInDays = null) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Kind == ApiTokenKind.Agent && AgentName is null)
        {
            yield return new ValidationResult("Agent tokens need an agent name.", [nameof(AgentName)]);
        }

        if (Kind == ApiTokenKind.Personal && AgentName is not null)
        {
            yield return new ValidationResult("Only agent tokens have an agent name.", [nameof(AgentName)]);
        }
    }
}

/// <param name="Id">Token id, used to revoke it.</param>
/// <param name="Name">Label given at creation.</param>
/// <param name="Kind">Personal or agent.</param>
/// <param name="AgentName">Agent name for agent tokens.</param>
/// <param name="Hint">Prefix and last four characters of the secret.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="ExpiresAt">Expiry time, or null if it never expires.</param>
/// <param name="LastUsedAt">Last successful use (updated at most once a minute).</param>
public sealed record ApiTokenResponse(
    Guid Id,
    string Name,
    ApiTokenKind Kind,
    string? AgentName,
    string Hint,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt);

/// <param name="Token">The stored token.</param>
/// <param name="Secret">The token secret. Shown once; only its hash is stored.</param>
public sealed record CreatedApiTokenResponse(ApiTokenResponse Token, string Secret);

/// <summary>What an authenticated API token resolves to.</summary>
public sealed record ApiTokenIdentity(Guid TokenId, Guid UserId, string? AgentName);

public sealed class ApiTokenService(IDbContextFactory<FlowstateDbContext> dbFactory, TimeProvider clock)
{
    /// <summary>Every token starts with this, so the auth handler can tell tokens from JWTs.</summary>
    public const string CommonPrefix = "fs_";

    public const string PersonalPrefix = "fs_pat_";
    public const string AgentPrefix = "fs_agent_";

    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    public async Task<CreatedApiTokenResponse> CreateAsync(Guid userId, CreateApiTokenRequest request, CancellationToken ct)
    {
        var prefix = request.Kind == ApiTokenKind.Agent ? AgentPrefix : PersonalPrefix;
        var secret = prefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow();

        var token = new ApiToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Kind = request.Kind,
            Name = request.Name.Trim(),
            AgentName = request.Kind == ApiTokenKind.Agent ? request.AgentName : null,
            Hash = Hash(secret),
            Hint = prefix + "..." + secret[^4..],
            CreatedAt = now,
            ExpiresAt = request.ExpiresInDays is { } days ? now.AddDays(days) : null,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(ct);

        return new CreatedApiTokenResponse(ToResponse(token), secret);
    }

    /// <summary>The user's tokens that are not revoked, newest first.</summary>
    public async Task<IReadOnlyList<ApiTokenResponse>> ListAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ApiTokens.AsNoTracking()
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new ApiTokenResponse(
                t.Id, t.Name, t.Kind, t.AgentName, t.Hint, t.CreatedAt, t.ExpiresAt, t.LastUsedAt))
            .ToListAsync(ct);
    }

    /// <summary>Revokes one of the user's tokens. Idempotent; false if the token isn't the user's.</summary>
    public async Task<bool> RevokeAsync(Guid userId, Guid tokenId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        var exists = await db.ApiTokens.AnyAsync(t => t.Id == tokenId && t.UserId == userId, ct);
        if (!exists)
        {
            return false;
        }

        await db.ApiTokens
            .Where(t => t.Id == tokenId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        return true;
    }

    /// <summary>Revokes every token of a user, e.g. when they are deactivated (ADR-013 IMP-002).</summary>
    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        await db.ApiTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    /// <summary>
    /// Resolves a presented secret. Not cached, so revocation takes effect on the next request; the lookup is a
    /// unique-index hit.
    /// </summary>
    public async Task<ApiTokenIdentity?> AuthenticateAsync(string secret, CancellationToken ct)
    {
        if (!secret.StartsWith(PersonalPrefix, StringComparison.Ordinal)
            && !secret.StartsWith(AgentPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var hash = Hash(secret);
        var now = clock.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var token = await db.ApiTokens.AsNoTracking()
            .Where(t => t.Hash == hash)
            .Select(t => new { t.Id, t.UserId, t.AgentName, t.ExpiresAt, t.RevokedAt, t.LastUsedAt })
            .SingleOrDefaultAsync(ct);

        if (token is null || token.RevokedAt is not null || token.ExpiresAt <= now)
        {
            return null;
        }

        if (token.LastUsedAt is null || now - token.LastUsedAt > LastUsedResolution)
        {
            await db.ApiTokens
                .Where(t => t.Id == token.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), ct);
        }

        return new ApiTokenIdentity(token.Id, token.UserId, token.AgentName);
    }

    private static ApiTokenResponse ToResponse(ApiToken t) =>
        new(t.Id, t.Name, t.Kind, t.AgentName, t.Hint, t.CreatedAt, t.ExpiresAt, t.LastUsedAt);

    private static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
