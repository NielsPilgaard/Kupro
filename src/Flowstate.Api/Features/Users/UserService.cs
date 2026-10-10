using System.ComponentModel.DataAnnotations;
using Flowstate.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Flowstate.Api.Features.Users;

/// <summary>Identity claims from Keycloak used to provision or refresh a <see cref="User"/>.</summary>
public sealed record ExternalIdentity(string ExternalId, string Email, string DisplayName, string? Locale);

/// <param name="Id">Flowstate user id.</param>
/// <param name="Email">Login email.</param>
/// <param name="DisplayName">Name shown in the UI and activity log.</param>
/// <param name="Locale">UI language: <c>en</c> or <c>da</c>.</param>
/// <param name="TimeZone">IANA time zone id.</param>
/// <param name="Agent">Agent name when the caller is an agent token acting for this user; otherwise null.</param>
public sealed record MeResponse(Guid Id, string Email, string DisplayName, string Locale, string TimeZone, string? Agent);

/// <param name="DisplayName">New display name.</param>
/// <param name="Locale">UI language: <c>en</c> or <c>da</c>.</param>
/// <param name="TimeZone">IANA time zone id, e.g. <c>Europe/Copenhagen</c>.</param>
public sealed record UpdateMeRequest(
    [StringLength(200, MinimumLength = 1)] string? DisplayName = null,
    [AllowedValues("en", "da", null)] string? Locale = null,
    [StringLength(64)] string? TimeZone = null);

public sealed class UserService(IDbContextFactory<FlowstateDbContext> dbFactory, IMemoryCache cache, TimeProvider clock)
{
    public static readonly string[] SupportedLocales = ["en", "da"];
    public const string DefaultTimeZone = "Europe/Copenhagen";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Returns the user id for a Keycloak identity, creating the user on first login. Cached so a JWT request costs
    /// no query; on cache miss email and name are refreshed from the token.
    /// </summary>
    public async Task<Guid> EnsureUserAsync(ExternalIdentity identity, CancellationToken ct)
    {
        var cacheKey = "user:" + identity.ExternalId;
        if (cache.TryGetValue(cacheKey, out Guid cached))
        {
            return cached;
        }

        var locale = identity.Locale is { } l && SupportedLocales.Contains(l) ? l : "en";
        var newId = Guid.CreateVersion7();
        var now = clock.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Upsert: concurrent first requests from the same new user must not race into a unique violation.
        var ids = await db.Database.SqlQuery<Guid>($"""
            INSERT INTO users (id, external_id, email, display_name, locale, time_zone, created_at)
            VALUES ({newId}, {identity.ExternalId}, {identity.Email}, {identity.DisplayName}, {locale}, {DefaultTimeZone}, {now})
            ON CONFLICT (external_id) DO UPDATE SET email = EXCLUDED.email, display_name = EXCLUDED.display_name
            RETURNING id AS "Value"
            """).ToListAsync(ct);

        var id = ids.Single();
        cache.Set(cacheKey, id, CacheDuration);
        return id;
    }

    public async Task<MeResponse?> GetMeAsync(Guid userId, string? agent, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new MeResponse(u.Id, u.Email, u.DisplayName, u.Locale, u.TimeZone, agent))
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>Updates profile fields that are set; returns null for an unknown user or time zone.</summary>
    public async Task<MeResponse?> UpdateMeAsync(Guid userId, string? agent, UpdateMeRequest request, CancellationToken ct)
    {
        if (request.TimeZone is not null && !TimeZoneInfo.TryFindSystemTimeZoneById(request.TimeZone, out _))
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return null;
        }

        user.DisplayName = request.DisplayName ?? user.DisplayName;
        user.Locale = request.Locale ?? user.Locale;
        user.TimeZone = request.TimeZone ?? user.TimeZone;
        await db.SaveChangesAsync(ct);

        return new MeResponse(user.Id, user.Email, user.DisplayName, user.Locale, user.TimeZone, agent);
    }
}
