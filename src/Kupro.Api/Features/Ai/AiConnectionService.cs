using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Kupro.Api.Data;
using Kupro.Api.Features.Email;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Meetings;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kupro.Api.Features.Ai;

/// <summary>
/// A workspace's own AI connections and routes (ADR-022, task 035): connect, check, replace a key, route capabilities,
/// and mark a connection rejected when its vendor refuses the key. Access is checked by the adapters: people with
/// billing access change things, members read status.
/// </summary>
public sealed partial class AiConnectionService(
    IDbContextFactory<KuproDbContext> dbFactory,
    WorkspaceContext workspace,
    WorkspaceCache cache,
    AiKeys keys,
    IAiModels models,
    AiRouter router,
    MeetingBudget budget,
    IOptions<AiOptions> options,
    IEmailSender email,
    TimeProvider clock,
    ILogger<AiConnectionService> logger)
{
    private const int MaxLastError = 500;

    public static IReadOnlyList<AiProviderResponse> Providers() =>
        AiProviders.All.Select(p => new AiProviderResponse(p.Id, p.Name, p.IsCustom, p.Capabilities, p.TranscriptionModel, p.ChatModel)).ToList();

    public async Task<AiStatusResponse> StatusAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connections = await db.AiConnections.AsNoTracking().OrderBy(c => c.CreatedAt).ToListAsync(ct);
        var routes = await db.AiRoutes.AsNoTracking().ToListAsync(ct);
        var byId = connections.ToDictionary(c => c.Id);
        var list = new List<AiRouteResponse>();
        foreach (var capability in Enum.GetValues<AiCapability>())
        {
            var all = routes.FirstOrDefault(r => r.Capability == capability && r.Language.Length == 0);
            list.Add(all is null ? await KuproRouteAsync(capability, ct) : RouteResponse(all, byId[all.ConnectionId]));
            list.AddRange(routes.Where(r => r.Capability == capability && r.Language.Length > 0).OrderBy(r => r.Language)
                .Select(r => RouteResponse(r, byId[r.ConnectionId])));
        }

        var (period, usage) = await budget.UsageByPayerAsync(ct);
        return new AiStatusResponse(connections.Select(Response).ToList(), list, usage, period);
    }

    public async Task<IssueResult<AiConnectionResponse>> AddAsync(AddAiConnectionRequest request, CancellationToken ct)
    {
        if (AiProviders.Find(request.ProviderType) is not { } provider)
        {
            return new IssueError("ai_provider_unknown",
                $"No provider type '{request.ProviderType}'. Use one of: {string.Join(", ", AiProviders.All.Select(p => p.Id))}.");
        }

        if (string.IsNullOrWhiteSpace(request.Key))
        {
            return new IssueError("key_required", "Paste the secret key.");
        }

        if (!keys.Enabled)
        {
            return KeyStorageMissing();
        }

        string? endpoint = null;
        if (provider.IsCustom)
        {
            if (await EndpointErrorAsync(request.Endpoint, ct) is { } bad)
            {
                return bad;
            }

            if (!request.EuConfirmed)
            {
                return new IssueError("eu_confirmation_required",
                    "Confirm that the endpoint is in the EU/EEA and run by you or your processor (euConfirmed).");
            }

            endpoint = request.Endpoint!.Trim().TrimEnd('/');
        }

        var useFor = (request.UseFor ?? []).Distinct().ToList();
        var routeModels = new Dictionary<AiCapability, string?>();
        foreach (var capability in useFor)
        {
            var named = capability == AiCapability.Transcription ? request.TranscriptionModel : request.ChatModel;
            if (string.IsNullOrWhiteSpace(named) && provider.DefaultModel(capability) is null)
            {
                return ModelRequired(capability);
            }

            routeModels[capability] = string.IsNullOrWhiteSpace(named) ? null : named.Trim();
        }

        var now = clock.GetUtcNow();
        var connection = new AiConnection
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.RequireWorkspaceId(),
            ProviderType = provider.Id,
            Name = string.IsNullOrWhiteSpace(request.Name) ? provider.Name : request.Name.Trim(),
            Endpoint = endpoint,
            ProjectId = provider.IsCustom || string.IsNullOrWhiteSpace(request.ProjectId) ? null : request.ProjectId.Trim(),
            KeyHint = AiKeys.Hint(request.Key.Trim()),
            Status = AiConnectionStatus.Unverified,
            EuConfirmedById = provider.IsCustom ? workspace.MemberId : null,
            EuConfirmedAt = provider.IsCustom ? now : null,
            CreatedById = workspace.MemberId,
            CreatedAt = now,
        };
        connection.EncryptedKey = keys.Protect(connection.Id, request.Key.Trim());
        var wanted = useFor.Count > 0
            ? useFor.Select(c => routeModels[c] ?? provider.DefaultModel(c)!)
            : provider.Capabilities.Select(provider.DefaultModel).OfType<string>();
        await CheckAsync(connection, provider, request.Key.Trim(), wanted.Distinct().ToList(), ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.AiConnections.Add(connection);
        // Only a key that works is routed to: a rejected one would switch features off, and never back to Kupro's key.
        if (connection.Status == AiConnectionStatus.Healthy)
        {
            var existing = await db.AiRoutes.Where(r => useFor.Contains(r.Capability) && r.Language == "").ToListAsync(ct);
            db.AiRoutes.RemoveRange(existing);
            db.AiRoutes.AddRange(useFor.Select(c => new AiRoute
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = connection.WorkspaceId,
                Capability = c,
                Language = "",
                ConnectionId = connection.Id,
                Model = routeModels[c],
            }));
        }

        await db.SaveChangesAsync(ct);
        cache.Invalidate();
        if (provider.IsCustom)
        {
            LogEuConfirmed(logger, connection.WorkspaceId, connection.Id, workspace.MemberId, endpoint!);
        }

        return Response(connection);
    }

    public async Task<IssueResult<AiConnectionResponse>> SetKeyAsync(Guid id, SetAiConnectionKeyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Key))
        {
            return new IssueError("key_required", "Paste the secret key.");
        }

        if (!keys.Enabled)
        {
            return KeyStorageMissing();
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.AiConnections.SingleOrDefaultAsync(c => c.Id == id, ct) is not { } connection)
        {
            return NotFound(id);
        }

        var key = request.Key.Trim();
        connection.EncryptedKey = keys.Protect(connection.Id, key);
        connection.KeyHint = AiKeys.Hint(key);
        await CheckAsync(connection, AiProviders.Find(connection.ProviderType)!, key, await RoutedModelsAsync(db, connection, ct), ct);
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
        return Response(connection);
    }

    /// <summary>Asks the vendor again. A key that works again turns the connection back on.</summary>
    public async Task<IssueResult<AiConnectionResponse>> RecheckAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.AiConnections.SingleOrDefaultAsync(c => c.Id == id, ct) is not { } connection)
        {
            return NotFound(id);
        }

        var key = keys.Unprotect(connection.Id, connection.EncryptedKey);
        await CheckAsync(connection, AiProviders.Find(connection.ProviderType)!, key, await RoutedModelsAsync(db, connection, ct), ct);
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
        return Response(connection);
    }

    /// <summary>Deletes the connection and its routes: those capabilities go back to Kupro's route, within the budget.</summary>
    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var removed = await db.AiConnections.Where(c => c.Id == id).ExecuteDeleteAsync(ct) == 1;
        cache.Invalidate();
        return removed;
    }

    public async Task<IssueResult<AiRouteResponse>> SetRouteAsync(SetAiRouteRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.AiConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == request.ConnectionId, ct) is not { } connection)
        {
            return NotFound(request.ConnectionId);
        }

        var provider = AiProviders.Find(connection.ProviderType)!;
        if (!provider.Capabilities.Contains(request.Capability))
        {
            return new IssueError("capability_unsupported", $"{provider.Name} can't serve {request.Capability}.");
        }

        var model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim();
        if (model is null && provider.DefaultModel(request.Capability) is null)
        {
            return ModelRequired(request.Capability);
        }

        var language = Language(request.Language);
        var route = await db.AiRoutes.SingleOrDefaultAsync(r => r.Capability == request.Capability && r.Language == language, ct);
        if (route is null)
        {
            route = new AiRoute
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = workspace.RequireWorkspaceId(),
                Capability = request.Capability,
                Language = language,
            };
            db.AiRoutes.Add(route);
        }

        route.ConnectionId = connection.Id;
        route.Model = model;
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
        return RouteResponse(route, connection);
    }

    /// <summary>The capability goes back to the every-language route, or to Kupro's.</summary>
    public async Task<bool> RemoveRouteAsync(AiCapability capability, string? language, CancellationToken ct)
    {
        var lang = Language(language);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var removed = await db.AiRoutes.Where(r => r.Capability == capability && r.Language == lang).ExecuteDeleteAsync(ct) == 1;
        cache.Invalidate();
        return removed;
    }

    /// <summary>
    /// The vendor refused a workspace's key during a call (ADR-022 decision 7): the connection stops being used, and
    /// everyone with billing access gets one email. A conditional update, so concurrent calls send one email.
    /// </summary>
    public async Task RejectAsync(Guid connectionId, string error, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        var message = Truncate(error);
        var changed = await db.AiConnections.Where(c => c.Id == connectionId && c.Status != AiConnectionStatus.Rejected)
            .ExecuteUpdateAsync(c => c.SetProperty(x => x.Status, AiConnectionStatus.Rejected)
                .SetProperty(x => x.LastError, message).SetProperty(x => x.LastCheckedAt, now), ct);
        cache.Invalidate();
        if (changed == 1)
        {
            await EmailRejectedAsync(db, connectionId, message, ct);
        }
    }

    // ---------------------------------------------------------------- helpers

    private async Task CheckAsync(AiConnection connection, AiProviderType provider, string key, IReadOnlyList<string> wanted,
        CancellationToken ct)
    {
        connection.LastCheckedAt = clock.GetUtcNow();
        try
        {
            var available = await models.ListAsync(provider.BaseUrl(connection.Endpoint, connection.ProjectId), key, ct);
            var missing = wanted.Where(m => !available.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
            connection.Status = missing.Count == 0 ? AiConnectionStatus.Healthy : AiConnectionStatus.Unverified;
            connection.LastError = missing.Count == 0 ? null : Truncate($"The key works, but these models aren't available to it: {string.Join(", ", missing)}.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            connection.Status = AiConnectionStatus.Rejected;
            connection.LastError = Truncate($"{(int)ex.StatusCode!.Value} {ex.StatusCode}: {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Unreachable or slow: not proof the key is wrong. Saved unchecked, and not routed to.
            connection.Status = AiConnectionStatus.Unverified;
            connection.LastError = Truncate(ex.Message);
        }
    }

    private static async Task<IReadOnlyList<string>> RoutedModelsAsync(KuproDbContext db, AiConnection connection, CancellationToken ct)
    {
        var provider = AiProviders.Find(connection.ProviderType)!;
        var routes = await db.AiRoutes.AsNoTracking().Where(r => r.ConnectionId == connection.Id).ToListAsync(ct);
        return routes.Count == 0
            ? provider.Capabilities.Select(provider.DefaultModel).OfType<string>().Distinct().ToList()
            : routes.Select(r => r.Model ?? provider.DefaultModel(r.Capability)).OfType<string>().Distinct().ToList();
    }

    /// <summary>
    /// A custom endpoint on hosted Kupro (ADR-022 decision 6): https, and every address it resolves to public. Checked
    /// again on every call by the HTTP client, so a name that later resolves somewhere private is refused too.
    /// </summary>
    private async Task<IssueError?> EndpointErrorAsync(string? endpoint, CancellationToken ct)
    {
        var allowPrivate = options.Value.AllowPrivateAddresses;
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (allowPrivate && uri.Scheme == Uri.UriSchemeHttp)))
        {
            return new IssueError("endpoint_not_https", "Give the endpoint's full https:// address, such as https://ai.example.eu/v1. Kupro sends meeting audio to it.");
        }

        if (allowPrivate)
        {
            return null;
        }

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(uri.Host, ct);
        }
        catch (SocketException)
        {
            return new IssueError("endpoint_not_found", $"{uri.Host} doesn't resolve to an address.");
        }

        return addresses.Length > 0 && addresses.All(PublicAddresses.IsPublic)
            ? null
            : new IssueError("endpoint_not_public", $"{uri.Host} is not on the public internet, so Kupro can't reach it from its servers.");
    }

    private async Task<AiRouteResponse> KuproRouteAsync(AiCapability capability, CancellationToken ct)
    {
        // Language "" reads the server's default route; per-language server routes are Kupro's own business.
        var server = await router.ResolveAsync(capability, "", ct);
        var kupro = server is { PaidBy: AiPaidBy.Kupro };
        return new AiRouteResponse(capability, null, null, "Kupro", kupro ? server!.Route.Model : null, true, AiPaidBy.Kupro, kupro);
    }

    private static AiRouteResponse RouteResponse(AiRoute route, AiConnection connection)
    {
        var provider = AiProviders.Find(connection.ProviderType)!;
        var model = route.Model ?? provider.DefaultModel(route.Capability);
        return new AiRouteResponse(route.Capability, route.Language.Length == 0 ? null : route.Language, connection.Id, connection.Name,
            model, model is not null && model == provider.DefaultModel(route.Capability), AiPaidBy.Workspace,
            connection.Status != AiConnectionStatus.Rejected);
    }

    private static AiConnectionResponse Response(AiConnection c) =>
        new(c.Id, c.ProviderType, c.Name, c.Endpoint, c.ProjectId, c.KeyHint, c.Status, c.LastError, c.LastCheckedAt,
            c.EuConfirmedById, c.EuConfirmedAt, c.CreatedAt);

    private static string Language(string? language) => string.IsNullOrWhiteSpace(language) ? "" : language.Trim().ToLowerInvariant();

    private static string Truncate(string text) => text.Length <= MaxLastError ? text : text[..MaxLastError];

    private static IssueError NotFound(Guid id) => new("ai_connection_not_found", $"No AI connection {id} in this workspace.");

    private static IssueError KeyStorageMissing() => new("key_storage_not_configured",
        "This server has no key storage, so it can't keep AI keys. Set KeyStorage, or configure models for the whole server.");

    private static IssueError ModelRequired(AiCapability capability) =>
        new("model_required", $"This provider has no tested default for {capability}; name the model.");

    private async Task EmailRejectedAsync(KuproDbContext db, Guid connectionId, string error, CancellationToken ct)
    {
        var workspaceId = workspace.RequireWorkspaceId();
        var name = await db.Workspaces.Where(w => w.Id == workspaceId).Select(w => w.Name).SingleAsync(ct);
        var connection = await db.AiConnections.AsNoTracking().Where(c => c.Id == connectionId).Select(c => c.Name).SingleAsync(ct);
        var recipients = await db.Members.AsNoTracking()
            .Where(m => m.Kind == MemberKind.Human && m.Status == MemberStatus.Active
                && (m.Role == WorkspaceRole.Owner || m.CanManageBilling))
            .Select(m => db.Users.Where(u => u.Id == m.UserId).Select(u => new { u.Email, u.Locale }).FirstOrDefault())
            .ToListAsync(ct);
        foreach (var to in recipients.Where(r => r is not null))
        {
            try
            {
                await email.SendAsync(RejectedMessage(to!.Email, to.Locale, name, connection, error), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogEmailFailed(logger, workspaceId, ex);
            }
        }
    }

    private static EmailMessage RejectedMessage(string to, string locale, string workspaceName, string connection, string error) =>
        locale.StartsWith("da", StringComparison.OrdinalIgnoreCase)
            ? new EmailMessage(to, $"{connection} afviste nøglen i {workspaceName}",
                string.Create(CultureInfo.GetCultureInfo("da-DK"), $"{connection} afviste arbejdsområdets nøgle ({error}). Mikrofonen er slået fra, og tekst bliver til forslag med en enklere søgning på nøgleord. Kupro skifter ikke til sin egen nøgle. Udskift nøglen under Indstillinger → AI, eller fjern forbindelsen for at gå tilbage til de inkluderede timer."))
            : new EmailMessage(to, $"{connection} rejected the key in {workspaceName}",
                $"{connection} rejected the workspace's key ({error}). The microphone is off, and typed text turns into proposals with a simpler keyword matcher. Kupro doesn't switch to its own key. Replace the key in Settings → AI, or remove the connection to go back to the included hours.");

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Workspace {WorkspaceId} connected AI endpoint {ConnectionId} at {Endpoint}; member {MemberId} confirmed it is in the EU/EEA")]
    private static partial void LogEuConfirmed(ILogger logger, Guid workspaceId, Guid connectionId, Guid memberId, string endpoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI key rejection email for workspace {WorkspaceId} failed")]
    private static partial void LogEmailFailed(ILogger logger, Guid workspaceId, Exception exception);
}
