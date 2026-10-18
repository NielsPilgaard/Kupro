namespace Aspire.Hosting;

/// <summary>Meeting agent models (task 015): Scaleway Generative APIs, OpenAI-compatible (ADR-015).</summary>
internal static class MeetingAgent
{
    private const string Endpoint = "https://api.scaleway.ai/v1";
    private const string TranscriptionModel = "whisper-large-v3";
    private const string ExtractionModel = "mistral-small-3.2-24b-instruct-2506";

    /// <summary>
    /// One key for transcription and proposals. A model for one language only is more environment here:
    /// <c>MeetingAgent__{Transcription|Extraction}__Languages__&lt;lang&gt;__{Endpoint|Model|ApiKey}</c> (ADR-015 decision 4).
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

        return api;
    }
}
