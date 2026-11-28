using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Kupro.Api.Features.Meetings;

namespace Kupro.Api.Features.Ai;

/// <summary>
/// The models a key can use, from the vendor's <c>GET /models</c>: the check when a workspace saves or checks a
/// connection (ADR-022 decision 4). An interface because it is an external system that tests replace.
/// </summary>
public interface IAiModels
{
    /// <summary>Throws <see cref="HttpRequestException"/> with the vendor's status when it refuses the key.</summary>
    Task<IReadOnlyList<string>> ListAsync(string baseUrl, string key, CancellationToken ct);
}

/// <summary>OpenAI-compatible <c>/models</c>, through the public-address guard like every call on a workspace's connection.</summary>
public sealed class OpenAiModels(IHttpClientFactory clients) : IAiModels
{
    public async Task<IReadOnlyList<string>> ListAsync(string baseUrl, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await clients.CreateClient(AiHttp.Workspace).SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        return body?["data"] is JsonArray data
            ? data.Select(m => m?["id"]?.GetValue<string>()).OfType<string>().ToList()
            : [];
    }
}

/// <summary>Self-hosted only: lets workspace connections reach private addresses, such as a model server on the same network.</summary>
public sealed class AiOptions
{
    public const string Section = "Ai";

    public bool AllowPrivateAddresses { get; set; }
}
