using Kupro.Api.Common;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Kupro.Api.Features.Voice;

/// <summary>Voice control: push to talk anywhere in the app (task 036). Interpreting changes nothing.</summary>
[ApiController]
[Route("api/v1/workspaces/{workspace}/voice")]
public sealed class VoiceController(VoiceService voice) : ControllerBase
{
    /// <summary>Upper bound for one utterance: 30 s of 16 kHz mono 16-bit WAV is about 1 MB.</summary>
    private const long MaxAudioBytes = 2 * 1024 * 1024;

    private const string NoMcp = "Agents call the issue tools directly; natural language to an agent's own tools adds nothing.";

    /// <summary>
    /// Read one utterance as text: proposed issue changes (<c>operations</c>), a place to open (<c>navigate</c>), or
    /// <c>none</c> with the reason. Nothing is applied; apply proposals through the issue endpoints.
    /// </summary>
    [HttpPost("interpret", Name = "interpretVoice")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [NoAgentSurface(AgentSurface.Mcp, NoMcp)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<VoiceInterpretation>> Interpret(VoiceInterpretRequest request, CancellationToken ct) =>
        this.ToResult(await voice.InterpretAsync(request, ct));

    /// <summary>One spoken utterance as WAV (16 kHz mono, under 30 s), transcribed and read like text. The audio isn't kept.</summary>
    [HttpPost("interpret/audio", Name = "interpretVoiceAudio")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [Consumes("audio/wav")]
    [RequestSizeLimit(MaxAudioBytes)]
    [NoAgentSurface(AgentSurface.Cli | AgentSurface.Mcp, "Audio capture is browser-only; the CLI sends text to interpretVoice.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<VoiceInterpretation>> InterpretAudio([FromQuery] VoiceAudioQuery query, CancellationToken ct) =>
        this.ToResult(await voice.InterpretAudioAsync(Request.Body, query, ct));

    /// <summary>Dictation: one utterance as WAV to text, for the focused field. With <c>tidy</c>, also a suggestion that only fixes spelling and punctuation.</summary>
    [HttpPost("transcribe", Name = "transcribeVoice")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [Consumes("audio/wav")]
    [RequestSizeLimit(MaxAudioBytes)]
    [NoAgentSurface(AgentSurface.Cli | AgentSurface.Mcp, "Dictation is audio into a browser text field; agents write text directly.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<VoiceTranscription>> Transcribe([FromQuery] VoiceTranscribeQuery query, CancellationToken ct) =>
        this.ToResult(await voice.TranscribeAsync(Request.Body, query, ct));
}
