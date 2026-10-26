namespace Aspire.Hosting;

/// <summary>Meeting agent models (task 015): Scaleway Generative APIs, OpenAI-compatible (ADR-015).</summary>
internal static class MeetingAgent
{
    private const string Endpoint = "https://api.scaleway.ai/v1";
    private const string TranscriptionModel = "whisper-large-v3";
    private const string ExtractionModel = "deepseek-v4.1-flash";

    /// <summary>Low keeps the slowest proposals inside the 5 s budget at the same precision (ADR-015 IMP-006).</summary>
    private const string ExtractionReasoningEffort = "low";

    // Scaleway's prices for the models above (.claude/skills/pricing/pricing.json): change them with the model.
    private const string EuroPerAudioMinute = "0.003";
    private const string EuroPerMillionInputTokens = "0.6";
    private const string EuroPerMillionCachedInputTokens = "0.12";
    private const string EuroPerMillionOutputTokens = "1.8";

    // The AI budget (ADR-013, task 017): 2 hours per seat a month, an hour worth what the pricing calculator says it
    // costs. The daily ceiling stops every workspace before a runaway day costs more than revenue can carry.
    private const string HoursPerSeat = "2";
    private const string EuroPerHour = "0.69";
    private const string DailyCeilingEuro = "10";

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

        api.WithEnvironment("MeetingAgent__Extraction__ReasoningEffort", ExtractionReasoningEffort)
            .WithEnvironment("MeetingAgent__Transcription__EuroPerAudioMinute", EuroPerAudioMinute)
            .WithEnvironment("MeetingAgent__Extraction__EuroPerMillionInputTokens", EuroPerMillionInputTokens)
            .WithEnvironment("MeetingAgent__Extraction__EuroPerMillionCachedInputTokens", EuroPerMillionCachedInputTokens)
            .WithEnvironment("MeetingAgent__Extraction__EuroPerMillionOutputTokens", EuroPerMillionOutputTokens)
            .WithEnvironment("MeetingAgent__Budget__HoursPerSeat", HoursPerSeat)
            .WithEnvironment("MeetingAgent__Budget__EuroPerHour", EuroPerHour)
            .WithEnvironment("MeetingAgent__Budget__DailyCeilingEuro", DailyCeilingEuro);
        return api;
    }
}
