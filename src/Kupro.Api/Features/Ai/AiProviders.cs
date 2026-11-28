namespace Kupro.Api.Features.Ai;

/// <summary>
/// A feature's need for a model, named once (ADR-022 decision 2). A feature asks <see cref="AiRouter"/> for its
/// capability and never reads keys or configuration itself.
/// </summary>
public enum AiCapability
{
    /// <summary>Audio to text: meeting audio now; voice commands and dictation in task 036.</summary>
    Transcription,

    /// <summary>Chat with a JSON schema: turns what was said in a meeting into proposed issue changes.</summary>
    MeetingProposals,

    /// <summary>Chat with a JSON schema: push-to-talk commands outside meetings (task 036).</summary>
    VoiceCommands,
}

/// <summary>Who pays for a call: Kupro's own key within the AI budget, or the workspace's own connection, unlimited.</summary>
public enum AiPaidBy
{
    Kupro,
    Workspace,
}

/// <summary>What a vendor offers, from the provider registry (ADR-022 decision 1).</summary>
/// <param name="Id">Stored on connections: never rename one.</param>
/// <param name="FixedEndpoint">The vendor's base URL; null means the workspace enters its own.</param>
/// <param name="TranscriptionModel">Kupro's tested default; null means a route must name the model.</param>
/// <param name="ChatModel">Kupro's tested default for chat capabilities; null means a route must name the model.</param>
/// <param name="ReasoningEffort">Sent with <see cref="ChatModel"/> only, not with a model a route names.</param>
public sealed record AiProviderType(
    string Id,
    string Name,
    string? FixedEndpoint,
    IReadOnlyList<AiCapability> Capabilities,
    string? TranscriptionModel,
    string? ChatModel,
    string? ReasoningEffort,
    decimal? EuroPerAudioMinute,
    decimal? EuroPerMillionInputTokens,
    decimal? EuroPerMillionCachedInputTokens,
    decimal? EuroPerMillionOutputTokens)
{
    public bool IsCustom => FixedEndpoint is null;

    /// <summary>The model Kupro tested for a capability, if any.</summary>
    public string? DefaultModel(AiCapability capability) =>
        capability == AiCapability.Transcription ? TranscriptionModel : ChatModel;

    /// <summary>The base URL calls go to: the fixed one (with a Scaleway project, if given) or the workspace's own.</summary>
    public string BaseUrl(string? customEndpoint, string? projectId) => FixedEndpoint is null
        ? customEndpoint!.TrimEnd('/')
        : string.IsNullOrWhiteSpace(projectId) ? FixedEndpoint : FixedEndpoint.Replace("/v1", $"/{projectId.Trim()}/v1", StringComparison.Ordinal);
}

/// <summary>
/// The provider types Kupro offers (ADR-022 decision 1). Adding a vendor is an entry here and a Chosen row in the
/// ADR-006 register; an adapter only if its API is not OpenAI-compatible.
/// </summary>
public static class AiProviders
{
    public const string ScalewayId = "scaleway";
    public const string OpenAiCompatibleId = "openai-compatible";

    /// <summary>
    /// Scaleway Generative APIs in Paris with the ADR-015 models. The rates are Scaleway's list prices, used to estimate
    /// what a workspace's own key costs it; change them together with <c>src/Kupro.AppHost/MeetingAgent.cs</c> and the
    /// pricing skill's <c>pricing.json</c>.
    /// </summary>
    public static readonly AiProviderType Scaleway = new(
        ScalewayId, "Scaleway", "https://api.scaleway.ai/v1",
        [AiCapability.Transcription, AiCapability.MeetingProposals, AiCapability.VoiceCommands],
        TranscriptionModel: "whisper-large-v3", ChatModel: "deepseek-v4.1-flash", ReasoningEffort: "low",
        EuroPerAudioMinute: 0.003m, EuroPerMillionInputTokens: 0.6m, EuroPerMillionCachedInputTokens: 0.12m,
        EuroPerMillionOutputTokens: 1.8m);

    /// <summary>
    /// Any OpenAI-compatible endpoint the workspace runs or chose, such as its own Whisper or vLLM server. No defaults:
    /// a route names its model. No prices: usage shows minutes and tokens only.
    /// </summary>
    public static readonly AiProviderType OpenAiCompatible = new(
        OpenAiCompatibleId, "OpenAI-compatible endpoint", FixedEndpoint: null,
        [AiCapability.Transcription, AiCapability.MeetingProposals, AiCapability.VoiceCommands],
        TranscriptionModel: null, ChatModel: null, ReasoningEffort: null,
        EuroPerAudioMinute: null, EuroPerMillionInputTokens: null, EuroPerMillionCachedInputTokens: null,
        EuroPerMillionOutputTokens: null);

    public static IReadOnlyList<AiProviderType> All { get; } = [Scaleway, OpenAiCompatible];

    public static AiProviderType? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}
