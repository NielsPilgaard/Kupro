using System.ComponentModel;
using Flowstate.Api.Common;
using Flowstate.Api.Features.Issues;
using Flowstate.Api.Features.Meetings;
using Flowstate.Api.Features.Workspaces;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Flowstate.Api.Mcp;

/// <summary>
/// Meeting agent tools (task 015), two of them to keep the tool count small (ADR-001): <c>meeting</c> to run a meeting
/// and feed it text, <c>review_proposals</c> to decide on what it proposes. Audio is browser-only.
/// </summary>
public sealed partial class FlowstateTools
{
    [McpServerTool(Name = "meeting"), Description(
        "The meeting agent turns what is said in a meeting into proposed issue changes. Actions: " +
        "start (project, type?, mode?, language?) | say (text): add what was said, returns proposals | " +
        "proposals (status?): before/after, transcript snippet, confidence; needsIssue=true means pick one of candidates | " +
        "get (transcript?) | list (state?) | update (state: live|paused|ended, mode: suggest|fullAuto). " +
        "Without meeting, say/update use the most recent live meeting and proposals/get the most recent one.")]
    [CoversOperations("startMeeting", "listMeetings", "getMeeting", "getMeetingTranscript", "updateMeeting",
        "addMeetingTranscript", "listMeetingProposals")]
    public async Task<object> Meeting(
        [Description("start, say, proposals, get, list or update.")] string action,
        [Description(MeetingHelp)] Guid? meeting = null,
        [Description("start: project key.")] string? project = null,
        [Description("start: standup, planning, refinement or other.")] MeetingType? type = null,
        [Description("start, update: suggest or fullAuto (an owner must enable full auto first).")] MeetingMode? mode = null,
        [Description("start: spoken language, en or da.")] string language = "en",
        [Description("say: what was said.")] string? text = null,
        [Description("list: live, paused or ended. update: the new state.")] MeetingState? state = null,
        [Description("proposals: pending, applied, rejected or undone.")] ProposalStatus? status = null,
        [Description("get: include the transcript.")] bool transcript = false,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken ct = default)
    {
        var reads = action is "proposals" or "get" or "list";
        await scope.EnterAsync(workspace, reads ? WorkspaceRole.Guest : WorkspaceRole.Member, ct);
        switch (action)
        {
            case "start" when project is not null:
                return Unwrap(await meetings.StartAsync(new StartMeetingRequest(project, type ?? MeetingType.Standup, mode, language), ct));
            case "say" when !string.IsNullOrWhiteSpace(text):
                return Unwrap(await meetings.AddTranscriptAsync(await MeetingIdAsync(meeting, live: true, ct), new TranscriptRequest(text), ct));
            case "proposals":
                var id = await MeetingIdAsync(meeting, live: false, ct);
                return await meetings.ListProposalsAsync(id, status, ct) ?? throw new McpException(Message(MeetingNotFound(id)));
            case "get":
                var getId = await MeetingIdAsync(meeting, live: false, ct);
                var found = await meetings.GetAsync(getId, ct) ?? throw new McpException(Message(MeetingNotFound(getId)));
                return transcript ? new { meeting = found, transcript = await meetings.TranscriptAsync(getId, ct) } : found;
            case "list":
                return await meetings.ListAsync(state, ct);
            case "update" when state is not null || mode is not null:
                return Unwrap(await meetings.UpdateAsync(await MeetingIdAsync(meeting, live: true, ct), new UpdateMeetingRequest(state, mode), ct));
            default:
                throw new McpException("invalid_arguments: start needs project; say needs text; update needs state or mode.");
        }
    }

    [McpServerTool(Name = "review_proposals"), Description(
        "Decide on a meeting's proposals, in order: approve applies the change as the meeting agent (value changes it: " +
        "state name, sprint id, member id, hours or text; issue picks a candidate), reject discards, edit saves without " +
        "applying, undo reverts an applied one. undoAll=true reverts everything the meeting applied. Each result has its own error.")]
    [CoversOperations("reviewMeetingProposals", "undoMeetingProposal", "undoMeeting")]
    public async Task<IReadOnlyList<ProposalReviewResult>> ReviewProposals(
        [Description("Decisions: { id, decision: approve|reject|edit|undo, value?, issue? }.")] IReadOnlyList<ProposalDecision>? items = null,
        [Description("Revert everything the meeting applied (ignores items).")] bool undoAll = false,
        [Description(MeetingHelp)] Guid? meeting = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken ct = default)
    {
        await scope.EnterAsync(workspace, WorkspaceRole.Member, ct);
        var id = await MeetingIdAsync(meeting, live: false, ct);
        if (undoAll)
        {
            return Unwrap(await meetings.UndoMeetingAsync(id, ct));
        }

        if (items is not { Count: > 0 })
        {
            throw new McpException("invalid_arguments: pass items, or undoAll=true.");
        }

        var results = new List<ProposalReviewResult>();
        foreach (var item in items)
        {
            if (item.Decision == "undo")
            {
                var undone = await meetings.UndoAsync(id, item.Id, ct);
                results.Add(undone.Error is { } error && undone.Value is null
                    ? Failed(await meetings.ListProposalsAsync(id, null, ct), item.Id, error)
                    : new ProposalReviewResult(undone.Value!, null));
                continue;
            }

            var decision = Enum.TryParse<ReviewDecision>(item.Decision, ignoreCase: true, out var d)
                ? d
                : throw new McpException($"invalid_arguments: unknown decision '{item.Decision}'.");
            results.AddRange(Unwrap(await meetings.ReviewAsync(id,
                new ReviewProposalsRequest([new ProposalReview(item.Id, decision, item.Value, item.Issue)]), ct)));
        }

        return results;
    }

    private const string MeetingHelp = "Meeting id. Default: the most recent (live) meeting.";

    /// <summary>One review_proposals item; <c>undo</c> is MCP's extra decision.</summary>
    /// <param name="Decision">approve, reject, edit or undo.</param>
    public sealed record ProposalDecision(Guid Id, string Decision, string? Value = null, string? Issue = null);

    private static ProposalReviewResult Failed(IReadOnlyList<ProposalResponse>? proposals, Guid id, IssueError error) =>
        proposals?.FirstOrDefault(p => p.Id == id) is { } p
            ? new ProposalReviewResult(p, error)
            : throw new McpException(Message(error));

    private static IssueError MeetingNotFound(Guid id) => new("meeting_not_found", $"No meeting {id}.");

    private async Task<Guid> MeetingIdAsync(Guid? meeting, bool live, CancellationToken ct)
    {
        if (meeting is { } id)
        {
            return id;
        }

        var recent = await meetings.ListAsync(live ? MeetingState.Live : null, ct);
        return recent.Count > 0
            ? recent[0].Id
            : throw new McpException(live ? "meeting_not_found: No live meeting. Start one with meeting action=start." : "meeting_not_found: No meetings yet.");
    }
}
