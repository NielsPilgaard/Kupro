namespace Aspire.Hosting;

/// <summary>Meeting agent models (task 015): Scaleway Generative APIs, OpenAI-compatible (ADR-015).</summary>
internal static class MeetingAgent
{
    private const string Endpoint = "https://api.scaleway.ai/v1";
    private const string TranscriptionModel = "whisper-large-v3";
    private const string ExtractionModel = "deepseek-v4.1-flash";

    /// <summary>Low keeps the slowest proposals inside the 5 s budget at the same precision (ADR-015 IMP-006).</summary>
    private const string ExtractionReasoningEffort = "low";

    /// <summary>
    /// One key for transcription and proposals. A model for one language only is more environment here:
    /// <c>MeetingAgent__{Transcription|Extraction}__Languages__&lt;lang&gt;__{Endpoint|Model|ApiKey|ReasoningEffort}</c>
    /// (ADR-015 decision 4).
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithMeetingAgent(this IResourceBuilder<ProjectResource> api)
    {
        var key = api.ApplicationBuilder.AddParameter("scaleway-ai-key", secret: true);
        foreach (var (seam, model) in new[] { ("Transcription", TranscriptionModel), ("Extraction", ExtractionModel) })
        {
            api.WithEnvironment($"MeetingAgent__{seam}__Endpoint", Endpoint)
                .WithEnvironment($"MeetingAgent__{seam}__Model", model)
                .WithEnvironment($"MeetingAgent__{seam}__ApiKey", key);
        }

        api.WithEnvironment("MeetingAgent__Extraction__ReasoningEffort", ExtractionReasoningEffort);
        return api;
    }
}
