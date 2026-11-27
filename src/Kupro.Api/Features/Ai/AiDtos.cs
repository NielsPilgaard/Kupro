namespace Kupro.Api.Features.Ai;

/// <summary>A provider type a workspace can connect (ADR-022 decision 1).</summary>
/// <param name="CustomEndpoint">True when the workspace enters the endpoint itself.</param>
/// <param name="TranscriptionModel">Kupro's tested default; null means a route must name one.</param>
/// <param name="ChatModel">Kupro's tested default for proposals and voice commands; null means a route must name one.</param>
public sealed record AiProviderResponse(
    string Id, string Name, bool CustomEndpoint, IReadOnlyList<AiCapability> Capabilities, string? TranscriptionModel, string? ChatModel);

/// <summary>A workspace's own connection. The key is never returned: only its last four characters.</summary>
public sealed record AiConnectionResponse(
    Guid Id,
    string ProviderType,
    string Name,
    string? Endpoint,
    string? ProjectId,
    string KeyHint,
    AiConnectionStatus Status,
    string? LastError,
    DateTimeOffset? LastCheckedAt,
    Guid? EuConfirmedById,
    DateTimeOffset? EuConfirmedAt,
    DateTimeOffset CreatedAt);

/// <summary>What serves a capability.</summary>
/// <param name="Language">Null for every language.</param>
/// <param name="ConnectionId">Null when Kupro's own route serves it, within the AI budget.</param>
/// <param name="ConnectionName">"Kupro" for Kupro's own route.</param>
/// <param name="Model">Null when nothing serves the capability.</param>
/// <param name="TestedModel">False when a route names a model other than the provider type's tested default.</param>
/// <param name="Available">False when nothing serves it, or the workspace's connection was rejected.</param>
public sealed record AiRouteResponse(
    AiCapability Capability, string? Language, Guid? ConnectionId, string ConnectionName, string? Model, bool TestedModel,
    AiPaidBy PaidBy, bool Available);

/// <summary>This month's metered use, by who paid.</summary>
/// <param name="EstimatedEuro">At the provider types' list prices; zero for custom endpoints, which have none.</param>
public sealed record AiUsageResponse(AiPaidBy PaidBy, double AudioMinutes, long InputTokens, long OutputTokens, decimal EstimatedEuro);

/// <param name="Period">First day of the month the usage is for.</param>
public sealed record AiStatusResponse(
    IReadOnlyList<AiConnectionResponse> Connections, IReadOnlyList<AiRouteResponse> Routes, IReadOnlyList<AiUsageResponse> Usage,
    DateOnly Period);

/// <summary>Connect a provider. The key is checked with the vendor before anything is routed to it.</summary>
/// <param name="ProviderType"><c>scaleway</c> or <c>openai-compatible</c>.</param>
/// <param name="Key">The secret key. Stored encrypted; never returned.</param>
/// <param name="Endpoint">Custom endpoints only: the OpenAI-compatible base URL, https.</param>
/// <param name="ProjectId">Scaleway only, optional.</param>
/// <param name="EuConfirmed">Custom endpoints only, required: the endpoint is in the EU/EEA and run by the workspace or its processor.</param>
/// <param name="UseFor">Capabilities to route here (every language) once the check passes.</param>
/// <param name="TranscriptionModel">With <paramref name="UseFor"/>: the model for transcription, if not the tested default.</param>
/// <param name="ChatModel">With <paramref name="UseFor"/>: the model for proposals and voice commands, if not the tested default.</param>
public sealed record AddAiConnectionRequest(
    string ProviderType,
    string Key,
    string? Name = null,
    string? Endpoint = null,
    string? ProjectId = null,
    bool EuConfirmed = false,
    IReadOnlyList<AiCapability>? UseFor = null,
    string? TranscriptionModel = null,
    string? ChatModel = null);

public sealed record SetAiConnectionKeyRequest(string Key);

/// <summary>Route a capability to a connection.</summary>
/// <param name="Language">Only this meeting language (e.g. <c>da</c>); left out, every language.</param>
/// <param name="Model">Left out, the provider type's tested default.</param>
public sealed record SetAiRouteRequest(AiCapability Capability, Guid ConnectionId, string? Language = null, string? Model = null);
