using Flowstate.Api.Common;
using Flowstate.Api.Features.Issues;
using Flowstate.Api.Features.Workspaces;
using Flowstate.Api.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Flowstate.Api.Features.Meetings;

/// <summary>Meeting agent: meetings, transcript, proposals and their review (task 015).</summary>
[ApiController]
[Route("api/v1/workspaces/{workspace}")]
public sealed class MeetingsController(MeetingService meetings) : ControllerBase
{
    /// <summary>Upper bound for one utterance: 30 s of 16 kHz mono 16-bit WAV is about 1 MB.</summary>
    private const long MaxAudioBytes = 2 * 1024 * 1024;

    /// <summary>Start a meeting the agent listens to. Proposals target the given project.</summary>
    [HttpPost("meetings", Name = "startMeeting")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType<MeetingResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MeetingResponse>> Start(StartMeetingRequest request, CancellationToken ct) =>
        this.ToResult(await meetings.StartAsync(request, ct), StatusCodes.Status201Created);

    /// <summary>Recent meetings, newest first.</summary>
    [HttpGet("meetings", Name = "listMeetings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<MeetingResponse>> List([FromQuery] MeetingState? state, CancellationToken ct) =>
        meetings.ListAsync(state, ct);

    /// <summary>One meeting with its proposal counts (the end-of-meeting summary).</summary>
    [HttpGet("meetings/{id:guid}", Name = "getMeeting")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MeetingResponse>> Get(Guid id, CancellationToken ct) =>
        await meetings.GetAsync(id, ct) is { } meeting ? meeting : this.ToProblem(new IssueError("meeting_not_found", $"No meeting {id}."));

    /// <summary>Pause, resume or end a meeting, or switch between suggest and full auto.</summary>
    [HttpPatch("meetings/{id:guid}", Name = "updateMeeting")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MeetingResponse>> Update(Guid id, UpdateMeetingRequest request, CancellationToken ct) =>
        this.ToResult(await meetings.UpdateAsync(id, request, ct));

    /// <summary>The meeting's transcript, oldest first (kept for the workspace's retention period).</summary>
    [HttpGet("meetings/{id:guid}/transcript", Name = "getMeetingTranscript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<TranscriptSegmentResponse>>> Transcript(Guid id, CancellationToken ct) =>
        await meetings.TranscriptAsync(id, ct) is { } segments ? Ok(segments) : this.ToProblem(new IssueError("meeting_not_found", $"No meeting {id}."));

    /// <summary>
    /// Add what was said, as text: from a meeting tool, an agent or a person typing. Returns the proposals it produced;
    /// in full auto some may already be applied.
    /// </summary>
    [HttpPost("meetings/{id:guid}/transcript", Name = "addMeetingTranscript")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TranscriptResult>> AddTranscript(Guid id, TranscriptRequest request, CancellationToken ct) =>
        this.ToResult(await meetings.AddTranscriptAsync(id, request, ct));

    /// <summary>One spoken utterance as WAV (16 kHz mono, under 30 s), transcribed and handled like text.</summary>
    [HttpPost("meetings/{id:guid}/audio", Name = "addMeetingAudio")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [Consumes("audio/wav")]
    [RequestSizeLimit(MaxAudioBytes)]
    [NoAgentSurface(AgentSurface.Cli | AgentSurface.Mcp, "Audio capture is browser-only; agents send text to addMeetingTranscript.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<TranscriptResult>> AddAudio(Guid id, CancellationToken ct) =>
        this.ToResult(await meetings.AddAudioAsync(id, Request.Body, ct));

    /// <summary>The meeting's proposals, oldest first.</summary>
    [HttpGet("meetings/{id:guid}/proposals", Name = "listMeetingProposals")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ProposalResponse>>> Proposals(Guid id, [FromQuery] ProposalStatus? status, CancellationToken ct) =>
        await meetings.ListProposalsAsync(id, status, ct) is { } proposals ? Ok(proposals) : this.ToProblem(new IssueError("meeting_not_found", $"No meeting {id}."));

    /// <summary>
    /// Approve, edit or reject proposals, in order. Approving applies the change as the meeting agent. Each result
    /// carries its own error; one failure doesn't stop the rest.
    /// </summary>
    [HttpPost("meetings/{id:guid}/proposals/review", Name = "reviewMeetingProposals")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ProposalReviewResult>>> Review(Guid id, ReviewProposalsRequest request, CancellationToken ct) =>
        this.ToResult(await meetings.ReviewAsync(id, request, ct));

    /// <summary>Revert an applied proposal. Refused when the field was changed again since.</summary>
    [HttpPost("meetings/{id:guid}/proposals/{proposalId:guid}/undo", Name = "undoMeetingProposal")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProposalResponse>> Undo(Guid id, Guid proposalId, CancellationToken ct) =>
        this.ToResult(await meetings.UndoAsync(id, proposalId, ct));

    /// <summary>Revert everything the meeting applied, newest first. Proposals changed since are reported, not reverted.</summary>
    [HttpPost("meetings/{id:guid}/undo", Name = "undoMeeting")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ProposalReviewResult>>> UndoMeeting(Guid id, CancellationToken ct) =>
        this.ToResult(await meetings.UndoMeetingAsync(id, ct));

    /// <summary>Default mode, full-auto permissions, transcript retention, and suggest-mode approval stats.</summary>
    [HttpGet("meeting-settings", Name = "getMeetingSettings")]
    [WorkspaceRole(WorkspaceRole.Member)]
    [NoAgentSurface(AgentSurface.Mcp, "Workspace admin configuration of the agent's own autonomy; people use the UI or CLI.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<MeetingSettingsResponse> Settings(CancellationToken ct) => meetings.GetSettingsAsync(ct);

    /// <summary>Change meeting settings. Owners only, since it decides what the agent may change without approval.</summary>
    [HttpPatch("meeting-settings", Name = "updateMeetingSettings")]
    [WorkspaceRole(WorkspaceRole.Owner)]
    [NoAgentSurface(AgentSurface.Mcp, "Workspace admin configuration of the agent's own autonomy; people use the UI or CLI.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<MeetingSettingsResponse>> UpdateSettings(UpdateMeetingSettingsRequest request, CancellationToken ct) =>
        this.ToResult(await meetings.UpdateSettingsAsync(request, ct));
}
