using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;
using Flowstate.Api.Data;
using Flowstate.Api.Features.Activity;
using Flowstate.Api.Features.Comments;
using Flowstate.Api.Features.Issues;
using Flowstate.Api.Features.Realtime;
using Flowstate.Api.Features.Sprints;
using Flowstate.Api.Features.Workspaces;
using Flowstate.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Flowstate.Api.Features.Meetings;

/// <summary>
/// Meeting agent (task 015, ADR-015). Owns meetings, transcripts, proposals and meeting settings. Proposals are applied
/// through <see cref="IssueService"/> and <see cref="CommentService"/> while acting as the meeting-agent member, so the
/// activity feed, realtime toasts and undo all see the agent as the actor.
/// </summary>
public sealed partial class MeetingService(
    IDbContextFactory<FlowstateDbContext> dbFactory,
    WorkspaceContext workspace,
    WorkspaceService workspaces,
    IssueService issues,
    CommentService comments,
    IProposalExtractor extractor,
    ISpeechToText speech,
    RealtimeEvents realtime,
    TimeProvider clock)
{
    public const string AgentName = "meeting-agent";

    /// <summary>Full auto applies only proposals at least this confident (task 015).</summary>
    public const double AutoApplyConfidence = 0.8;

    private const int RecentSegments = 6;
    private const int MaxCandidates = 3;
    private const int IssueHintLimit = 60;

    /// <summary>How far back an open proposal counts as the same one heard again, e.g. by a second microphone.</summary>
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(2);

    private static readonly ProposalOperation[] DefaultFullAutoOperations = [ProposalOperation.State, ProposalOperation.Sprint];

    // ---------------------------------------------------------------- meetings

    public async Task<IssueResult<MeetingResponse>> StartAsync(StartMeetingRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var projectKey = request.Project.Trim().ToUpperInvariant();
        var projectId = await db.Projects.Where(p => p.Key == projectKey).Select(p => (Guid?)p.Id).SingleOrDefaultAsync(ct);
        if (projectId is null)
        {
            return new IssueError("project_not_found", $"No project {projectKey}.");
        }

        var settings = await SettingsAsync(db, ct);
        var mode = request.Mode ?? settings.DefaultMode;
        if (mode == MeetingMode.FullAuto && !settings.FullAutoEnabled)
        {
            return FullAutoDisabled();
        }

        if (await AgentIdAsync(db, ct) is not { } agentId)
        {
            return new IssueError("meeting_agent_unavailable", "Couldn't set up the meeting agent in this workspace.");
        }

        var meeting = new MeetingSession
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.RequireWorkspaceId(),
            ProjectId = projectId.Value,
            Type = request.Type,
            Mode = mode,
            Language = request.Language,
            State = MeetingState.Live,
            StartedById = workspace.MemberId,
            AgentId = agentId,
            StartedAt = clock.GetUtcNow(),
        };
        db.Meetings.Add(meeting);
        await db.SaveChangesAsync(ct);
        await realtime.ChangedAsync(ChangeTopic.Meetings);
        return (await MeetingsAsync(db, db.Meetings.Where(m => m.Id == meeting.Id), ct))[0];
    }

    /// <summary>Most recent meetings first.</summary>
    public async Task<IReadOnlyList<MeetingResponse>> ListAsync(MeetingState? state, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.Meetings.AsNoTracking();
        if (state is { } s)
        {
            query = query.Where(m => m.State == s);
        }

        return await MeetingsAsync(db, query.OrderByDescending(m => m.StartedAt).Take(50), ct);
    }

    public async Task<MeetingResponse?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await MeetingsAsync(db, db.Meetings.AsNoTracking().Where(m => m.Id == id), ct)).SingleOrDefault();
    }

    /// <summary>Pause, resume, end, or switch mode. An ended meeting stays ended.</summary>
    public async Task<IssueResult<MeetingResponse>> UpdateAsync(Guid id, UpdateMeetingRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var meeting = await db.Meetings.SingleOrDefaultAsync(m => m.Id == id, ct);
        if (meeting is null)
        {
            return MeetingNotFound(id);
        }

        if (meeting.State == MeetingState.Ended && request.State is not (null or MeetingState.Ended))
        {
            return new IssueError("meeting_ended", "This meeting has ended. Start a new one.");
        }

        if (request.Mode == MeetingMode.FullAuto && !(await SettingsAsync(db, ct)).FullAutoEnabled)
        {
            return FullAutoDisabled();
        }

        meeting.Mode = request.Mode ?? meeting.Mode;
        var ending = request.State == MeetingState.Ended && meeting.State != MeetingState.Ended;
        if (request.State is { } state && state != meeting.State)
        {
            meeting.State = state;
            meeting.EndedAt = state == MeetingState.Ended ? clock.GetUtcNow() : null;
        }

        await db.SaveChangesAsync(ct);
        if (ending && (await SettingsAsync(db, ct)).TranscriptRetentionDays == 0)
        {
            // No retention: the transcript goes with the meeting (privacy policy). The hourly job catches failures here.
            await db.TranscriptSegments.Where(t => t.SessionId == id).ExecuteDeleteAsync(ct);
            await db.MeetingProposals.Where(p => p.SessionId == id && p.Snippet != null)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.Snippet, (string?)null), ct);
        }

        await realtime.ChangedAsync(ChangeTopic.Meetings);
        return (await MeetingsAsync(db, db.Meetings.Where(m => m.Id == id), ct))[0];
    }

    public async Task<IReadOnlyList<TranscriptSegmentResponse>?> TranscriptAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Meetings.AnyAsync(m => m.Id == id, ct))
        {
            return null;
        }

        var segments = await db.TranscriptSegments.AsNoTracking().Where(t => t.SessionId == id).OrderBy(t => t.CreatedAt)
            .ToListAsync(ct);
        var speakers = await MemberRefs.ByIdAsync(db, segments.Select(t => t.SpeakerId), ct);
        return segments.Select(t => new TranscriptSegmentResponse(t.Id, speakers[t.SpeakerId], t.Text, t.CreatedAt)).ToList();
    }

    // ---------------------------------------------------------------- listening

    /// <summary>Transcribes one utterance (WAV) and treats it like text. Browser only (task 015 parity opt-out).</summary>
    public async Task<IssueResult<TranscriptResult>> AddAudioAsync(Guid id, Stream wav, CancellationToken ct)
    {
        string language;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var meeting = await db.Meetings.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, ct);
            if (meeting is null)
            {
                return MeetingNotFound(id);
            }

            if (meeting.State != MeetingState.Live)
            {
                return NotLive();
            }

            language = meeting.Language;
        }

        if (!speech.Supports(language))
        {
            return new IssueError("transcription_unavailable", $"No speech-to-text model is configured for '{language}'.");
        }

        var text = await speech.TranscribeAsync(wav, language, ct);
        return string.IsNullOrWhiteSpace(text) ? new TranscriptResult("", []) : await AddTranscriptAsync(id, new TranscriptRequest(text), ct);
    }

    /// <summary>
    /// Adds what was said and turns it into proposals. In full auto, confident proposals for allowed operations apply
    /// at once; ambiguous, low-confidence, destructive and create-issue proposals still wait.
    /// </summary>
    public async Task<IssueResult<TranscriptResult>> AddTranscriptAsync(Guid id, TranscriptRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var meeting = await db.Meetings.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, ct);
        if (meeting is null)
        {
            return MeetingNotFound(id);
        }

        if (meeting.State != MeetingState.Live)
        {
            return NotLive();
        }

        var projectKey = await db.Projects.Where(p => p.Id == meeting.ProjectId).Select(p => p.Key).SingleAsync(ct);
        var text = NormalizeKeys(request.Text.Trim(), projectKey);
        var recent = await db.TranscriptSegments.AsNoTracking().Where(t => t.SessionId == id)
            .OrderByDescending(t => t.CreatedAt).Take(RecentSegments).Select(t => t.Text).ToListAsync(ct);
        recent.Reverse();

        var segment = new TranscriptSegment
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = meeting.WorkspaceId,
            SessionId = id,
            SpeakerId = workspace.MemberId,
            Text = text,
            CreatedAt = clock.GetUtcNow(),
        };

        var context = await ContextAsync(db, meeting, recent, text, ct);
        var extracted = await extractor.ExtractAsync(context, ct);
        var heard = new List<MeetingProposal>();
        foreach (var action in extracted)
        {
            if (await ProposeAsync(db, meeting, segment, projectKey, action, ct) is { } proposal)
            {
                heard.Add(proposal);
            }
        }

        var proposals = new List<MeetingProposal>();
        // Retrying execution strategy: the transaction must run inside it as one retriable unit.
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            proposals.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Several microphones in one room hear the same sentence at once. Serialise per meeting, so the first
            // proposal wins and the copies from the other microphones are dropped.
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({id.ToString()}, 0))", ct);
            var since = segment.CreatedAt - DuplicateWindow;
            var open = await db.MeetingProposals.AsNoTracking()
                .Where(p => p.SessionId == id && p.CreatedAt >= since
                    && (p.Status == ProposalStatus.Pending || p.Status == ProposalStatus.Applied))
                .ToListAsync(ct);
            proposals.AddRange(heard.Where(h => !open.Any(o => SameAction(o, h))));

            db.TranscriptSegments.Add(segment);
            db.MeetingProposals.AddRange(proposals);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        var settings = await SettingsAsync(db, ct);
        foreach (var proposal in proposals.Where(p => AutoApplies(meeting, settings, p)))
        {
            // A proposal that fails to apply stays pending for a person to look at.
            if (await ApplyAsync(db, meeting, proposal, reviewer: null, ct) is null)
            {
                proposal.AutoApplied = true;
            }
        }

        await db.SaveChangesAsync(ct);
        await realtime.ChangedAsync(ChangeTopic.Meetings);
        return new TranscriptResult(text, await ProposalsAsync(db, proposals, ct));
    }

    // ---------------------------------------------------------------- proposals

    public async Task<IReadOnlyList<ProposalResponse>?> ListProposalsAsync(Guid id, ProposalStatus? status, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Meetings.AnyAsync(m => m.Id == id, ct))
        {
            return null;
        }

        var query = db.MeetingProposals.AsNoTracking().Where(p => p.SessionId == id);
        if (status is { } s)
        {
            query = query.Where(p => p.Status == s);
        }

        return await ProposalsAsync(db, await query.OrderBy(p => p.CreatedAt).ToListAsync(ct), ct);
    }

    /// <summary>Approve (applying any edit), reject or edit proposals, in order. A failed item doesn't stop the rest.</summary>
    public async Task<IssueResult<IReadOnlyList<ProposalReviewResult>>> ReviewAsync(
        Guid id, ReviewProposalsRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var meeting = await db.Meetings.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, ct);
        if (meeting is null)
        {
            return MeetingNotFound(id);
        }

        var ids = request.Items.Select(i => i.Id).ToList();
        var proposals = await db.MeetingProposals.Where(p => p.SessionId == id && ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var projectKey = await db.Projects.Where(p => p.Id == meeting.ProjectId).Select(p => p.Key).SingleAsync(ct);
        var reviewer = workspace.MemberId;
        var outcomes = new List<(MeetingProposal? Proposal, Guid Id, IssueError? Error)>();
        foreach (var item in request.Items)
        {
            if (!proposals.TryGetValue(item.Id, out var proposal))
            {
                outcomes.Add((null, item.Id, ProposalNotFound(item.Id)));
                continue;
            }

            outcomes.Add((proposal, item.Id, await ReviewOneAsync(db, meeting, projectKey, proposal, item, reviewer, ct)));
            await db.SaveChangesAsync(ct);
        }

        await realtime.ChangedAsync(ChangeTopic.Meetings);
        var responses = (await ProposalsAsync(db, outcomes.Where(o => o.Proposal is not null).Select(o => o.Proposal!).Distinct().ToList(), ct))
            .ToDictionary(r => r.Id);
        var results = new List<ProposalReviewResult>();
        foreach (var (proposal, itemId, error) in outcomes)
        {
            if (proposal is null)
            {
                return error!;
            }

            results.Add(new ProposalReviewResult(responses[itemId], error));
        }

        return results;
    }

    /// <summary>Reverts an applied proposal, unless someone changed the field since.</summary>
    public async Task<IssueResult<ProposalResponse>> UndoAsync(Guid id, Guid proposalId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var meeting = await db.Meetings.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, ct);
        var proposal = await db.MeetingProposals.SingleOrDefaultAsync(p => p.Id == proposalId && p.SessionId == id, ct);
        if (meeting is null || proposal is null)
        {
            return meeting is null ? MeetingNotFound(id) : ProposalNotFound(proposalId);
        }

        if (await RevertAsync(db, meeting, proposal, ct) is { } error)
        {
            return error;
        }

        await db.SaveChangesAsync(ct);
        await realtime.ChangedAsync(ChangeTopic.Meetings);
        return (await ProposalsAsync(db, [proposal], ct))[0];
    }

    /// <summary>Reverts every applied proposal of the meeting, newest first. Ones changed since are left and reported.</summary>
    public async Task<IssueResult<IReadOnlyList<ProposalReviewResult>>> UndoMeetingAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var meeting = await db.Meetings.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, ct);
        if (meeting is null)
        {
            return MeetingNotFound(id);
        }

        var applied = await db.MeetingProposals.Where(p => p.SessionId == id && p.Status == ProposalStatus.Applied)
            .OrderByDescending(p => p.AppliedAt).ToListAsync(ct);
        var errors = new Dictionary<Guid, IssueError>();
        foreach (var proposal in applied)
        {
            if (await RevertAsync(db, meeting, proposal, ct) is { } error)
            {
                errors[proposal.Id] = error;
            }

            await db.SaveChangesAsync(ct);
        }

        await realtime.ChangedAsync(ChangeTopic.Meetings);
        return (await ProposalsAsync(db, applied, ct)).Select(p => new ProposalReviewResult(p, errors.GetValueOrDefault(p.Id))).ToList();
    }

    // ---------------------------------------------------------------- settings

    public async Task<MeetingSettingsResponse> GetSettingsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await SettingsAsync(db, ct);
        var decided = await db.MeetingProposals.AsNoTracking()
            .Where(p => !p.AutoApplied && p.Status != ProposalStatus.Pending)
            .GroupBy(p => p.Operation)
            .Select(g => new OperationStats(
                g.Key,
                g.Count(),
                g.Count(p => p.Status != ProposalStatus.Rejected && !p.Edited),
                g.Count(p => p.Status != ProposalStatus.Rejected && p.Edited),
                g.Count(p => p.Status == ProposalStatus.Rejected)))
            .ToListAsync(ct);
        var stats = Enum.GetValues<ProposalOperation>()
            .Select(op => decided.FirstOrDefault(s => s.Operation == op) ?? new OperationStats(op, 0, 0, 0, 0)).ToList();
        return new MeetingSettingsResponse(settings.DefaultMode, settings.FullAutoEnabled, settings.FullAutoOperations,
            settings.TranscriptRetentionDays, stats);
    }

    /// <summary>Owners only (controller). Creating issues never applies without approval, so it can't be allowed.</summary>
    public async Task<IssueResult<MeetingSettingsResponse>> UpdateSettingsAsync(UpdateMeetingSettingsRequest request, CancellationToken ct)
    {
        if (request.FullAutoOperations?.Contains(ProposalOperation.CreateIssue) == true)
        {
            return new IssueError("invalid_operation", "Creating issues always waits for approval.");
        }

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var settings = await db.MeetingSettings.SingleOrDefaultAsync(ct);
            if (settings is null)
            {
                settings = NewSettings(workspace.RequireWorkspaceId());
                db.MeetingSettings.Add(settings);
            }

            settings.DefaultMode = request.DefaultMode ?? settings.DefaultMode;
            settings.FullAutoEnabled = request.FullAutoEnabled ?? settings.FullAutoEnabled;
            settings.FullAutoOperations = request.FullAutoOperations?.Distinct().ToList() ?? settings.FullAutoOperations;
            settings.TranscriptRetentionDays = request.TranscriptRetentionDays ?? settings.TranscriptRetentionDays;
            if (!settings.FullAutoEnabled && settings.DefaultMode == MeetingMode.FullAuto)
            {
                return FullAutoDisabled();
            }

            await db.SaveChangesAsync(ct);
        }

        return await GetSettingsAsync(ct);
    }

    /// <summary>
    /// Deletes transcripts and proposal snippets past each workspace's retention. Background job, so it reads across
    /// workspaces. With retention 0 (the default) they go when the meeting ends, or after a day if nobody ends it.
    /// </summary>
    public static async Task<int> PurgeExpiredTranscriptsAsync(FlowstateDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        // System job across all tenants: no request workspace exists, and each row is matched to its own workspace's setting.
        var segments = db.TranscriptSegments.IgnoreQueryFilters([FlowstateDbContext.TenantFilter]);
        var settings = db.MeetingSettings.IgnoreQueryFilters([FlowstateDbContext.TenantFilter]);
        var proposals = db.MeetingProposals.IgnoreQueryFilters([FlowstateDbContext.TenantFilter]);
        var meetings = db.Meetings.IgnoreQueryFilters([FlowstateDbContext.TenantFilter]);
        var abandoned = now.AddDays(-1);

        var deleted = await segments
            .Where(t => settings.Any(s => s.WorkspaceId == t.WorkspaceId && s.TranscriptRetentionDays > 0)
                ? settings.Any(s => s.WorkspaceId == t.WorkspaceId && t.CreatedAt < now.AddDays(-s.TranscriptRetentionDays))
                : t.CreatedAt < abandoned || meetings.Any(m => m.Id == t.SessionId && m.State == MeetingState.Ended))
            .ExecuteDeleteAsync(ct);
        await proposals
            .Where(p => p.Snippet != null)
            .Where(p => settings.Any(s => s.WorkspaceId == p.WorkspaceId && s.TranscriptRetentionDays > 0)
                ? settings.Any(s => s.WorkspaceId == p.WorkspaceId && p.CreatedAt < now.AddDays(-s.TranscriptRetentionDays))
                : p.CreatedAt < abandoned || meetings.Any(m => m.Id == p.SessionId && m.State == MeetingState.Ended))
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.Snippet, (string?)null), ct);
        return deleted;
    }

    // ---------------------------------------------------------------- internals

    private async Task<IssueError?> ReviewOneAsync(FlowstateDbContext db, MeetingSession meeting, string projectKey,
        MeetingProposal proposal, ProposalReview item, Guid reviewer, CancellationToken ct)
    {
        if (proposal.Status != ProposalStatus.Pending)
        {
            return new IssueError("proposal_not_pending", $"Proposal {proposal.Id} is already {Camel(proposal.Status)}.");
        }

        if (item.Decision == ReviewDecision.Reject)
        {
            proposal.Status = ProposalStatus.Rejected;
            proposal.ReviewedById = reviewer;
            proposal.ReviewedAt = clock.GetUtcNow();
            return null;
        }

        if (item.Issue is { } key)
        {
            var normalized = key.Trim().ToUpperInvariant();
            var issueId = await db.Issues.Where(i => i.Key == normalized).Select(i => (Guid?)i.Id).SingleOrDefaultAsync(ct);
            if (issueId is null || proposal.Operation == ProposalOperation.CreateIssue)
            {
                return issueId is null ? IssueError.NotFound(normalized) : new IssueError("invalid_issue", "A new issue has no target issue.");
            }

            proposal.Edited |= proposal.IssueId != issueId;
            proposal.IssueId = issueId;
            proposal.Before = await CurrentAsync(db, issueId.Value, proposal.Operation, ct);
        }

        if (item.Value is { } raw)
        {
            if (await NormalizeValueAsync(db, meeting, proposal.Operation, raw, ct) is not { } value)
            {
                return new IssueError("invalid_value", $"'{raw}' isn't a valid {Camel(proposal.Operation)} value.");
            }

            proposal.Edited |= proposal.Value != value;
            proposal.Value = value;
        }

        if (item.Decision == ReviewDecision.Edit)
        {
            return null;
        }

        if (proposal.IssueId is null && proposal.Operation != ProposalOperation.CreateIssue)
        {
            return new IssueError("issue_required", "Pick the issue this proposal is about first.");
        }

        _ = projectKey;
        return await ApplyAsync(db, meeting, proposal, reviewer, ct);
    }

    /// <summary>Applies as the meeting agent. On success the proposal is marked applied; on failure it is unchanged.</summary>
    private async Task<IssueError?> ApplyAsync(FlowstateDbContext db, MeetingSession meeting, MeetingProposal proposal,
        Guid? reviewer, CancellationToken ct)
    {
        var key = proposal.IssueId is { } issueId
            ? await db.Issues.Where(i => i.Id == issueId).Select(i => i.Key).SingleAsync(ct)
            : null;
        if (key is not null)
        {
            proposal.Before = await CurrentAsync(db, proposal.IssueId!.Value, proposal.Operation, ct);
        }

        IssueError? error;
        using (workspace.ActAs(meeting.AgentId, Surface.Meeting, proposal.Id))
        {
            switch (proposal.Operation)
            {
                case ProposalOperation.Comment:
                    var comment = await comments.AddAsync(key!, new CommentRequest(proposal.Value!), ct);
                    error = comment.Error;
                    proposal.CommentId = comment.Value?.Id;
                    break;
                case ProposalOperation.CreateIssue:
                    var projectKey = await db.Projects.Where(p => p.Id == meeting.ProjectId).Select(p => p.Key).SingleAsync(ct);
                    var created = await issues.CreateAsync(new CreateIssueRequest(projectKey, proposal.Value!, State: IssueState.Todo), ct);
                    error = created.Error;
                    proposal.IssueId = created.Value?.Id;
                    break;
                default:
                    error = (await issues.UpdateAsync(key!, Change(proposal.Operation, proposal.Value!), ct)).Error;
                    break;
            }
        }

        if (error is not null)
        {
            return error;
        }

        proposal.Status = ProposalStatus.Applied;
        proposal.AppliedAt = clock.GetUtcNow();
        if (reviewer is { } r)
        {
            proposal.ReviewedById = r;
            proposal.ReviewedAt = proposal.AppliedAt;
        }

        return null;
    }

    private async Task<IssueError?> RevertAsync(FlowstateDbContext db, MeetingSession meeting, MeetingProposal proposal, CancellationToken ct)
    {
        if (proposal.Status != ProposalStatus.Applied)
        {
            return new IssueError("proposal_not_applied", $"Proposal {proposal.Id} isn't applied.");
        }

        var key = await db.Issues.Where(i => i.Id == proposal.IssueId).Select(i => i.Key).SingleOrDefaultAsync(ct);
        if (key is null)
        {
            return new IssueError("issue_not_found", "The issue no longer exists.");
        }

        var isField = proposal.Operation is not (ProposalOperation.Comment or ProposalOperation.CreateIssue);
        if (isField && await CurrentAsync(db, proposal.IssueId!.Value, proposal.Operation, ct) != proposal.Value)
        {
            return new IssueError("changed_since_applied", $"{key} was changed after the meeting agent did; undo it by hand.");
        }

        IssueError? error;
        using (workspace.ActAs(meeting.AgentId, Surface.Meeting, proposal.Id))
        {
            error = proposal.Operation switch
            {
                ProposalOperation.Comment when proposal.CommentId is { } commentId => await comments.DeleteAsync(commentId, ct),
                ProposalOperation.Comment => null,
                ProposalOperation.CreateIssue => (await issues.UpdateAsync(key, new UpdateIssueRequest(State: IssueState.Canceled), ct)).Error,
                _ => (await issues.UpdateAsync(key, Change(proposal.Operation, proposal.Before ?? "none"), ct)).Error,
            };
        }

        if (error is not null && error.Code != "comment_not_found")
        {
            return error;
        }

        proposal.Status = ProposalStatus.Undone;
        return null;
    }

    private static UpdateIssueRequest Change(ProposalOperation operation, string value) => operation switch
    {
        ProposalOperation.State => new UpdateIssueRequest(State: Enum.Parse<IssueState>(value, ignoreCase: true)),
        ProposalOperation.Sprint when value == "none" => new UpdateIssueRequest(Clear: [IssueField.Sprint]),
        ProposalOperation.Sprint => new UpdateIssueRequest(Sprint: value),
        ProposalOperation.Assignee when value == "none" => new UpdateIssueRequest(Clear: [IssueField.Assignee]),
        ProposalOperation.Assignee => new UpdateIssueRequest(Assignee: value),
        ProposalOperation.Estimate when value == "none" => new UpdateIssueRequest(Clear: [IssueField.Effort]),
        ProposalOperation.Estimate => new UpdateIssueRequest(Effort: decimal.Parse(value, CultureInfo.InvariantCulture)),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Not a field change."),
    };

    private static bool AutoApplies(MeetingSession meeting, MeetingSettings settings, MeetingProposal p) =>
        meeting.Mode == MeetingMode.FullAuto
        && settings.FullAutoEnabled
        && settings.FullAutoOperations.Contains(p.Operation)
        && p.Operation != ProposalOperation.CreateIssue
        && p.IssueId is not null
        && p.Confidence >= AutoApplyConfidence
        && !(p.Operation == ProposalOperation.State && p.Value == Camel(IssueState.Canceled));

    /// <summary>
    /// The same change heard twice. Field changes match exactly. The model words comments and new issue titles anew each
    /// time, so for those most of the words matching is enough.
    /// </summary>
    private static bool SameAction(MeetingProposal a, MeetingProposal b)
    {
        if (a.Operation != b.Operation)
        {
            return false;
        }

        // An applied new issue has its id; the copy of it doesn't, so only the title tells.
        var sameTarget = a.Operation == ProposalOperation.CreateIssue
            || (a.IssueId == b.IssueId && (a.IssueId is not null || a.Candidates.Order().SequenceEqual(b.Candidates.Order())));
        return sameTarget && a.Operation switch
        {
            ProposalOperation.Comment or ProposalOperation.CreateIssue => SimilarWords(a.Value, b.Value),
            _ => a.Value == b.Value,
        };
    }

    /// <summary>At least half the distinct words in common (Jaccard), ignoring case.</summary>
    private static bool SimilarWords(string? a, string? b)
    {
        static HashSet<string> Words(string? s) =>
            Word().Matches(s ?? "").Select(m => m.Value.ToLowerInvariant()).ToHashSet();

        var x = Words(a);
        var y = Words(b);
        var union = x.Union(y).Count();
        return union > 0 && x.Intersect(y).Count() * 2 >= union;
    }

    /// <summary>Turns an extracted action into a proposal, or null when its value can't be used.</summary>
    private async Task<MeetingProposal?> ProposeAsync(FlowstateDbContext db, MeetingSession meeting, TranscriptSegment segment,
        string projectKey, ExtractedAction action, CancellationToken ct)
    {
        if (await NormalizeValueAsync(db, meeting, action.Operation, action.Value, ct) is not { } value)
        {
            return null;
        }

        Guid? issueId = null;
        List<string> candidates = [];
        if (action.Operation != ProposalOperation.CreateIssue)
        {
            (issueId, candidates) = await ResolveIssueAsync(db, projectKey, action.IssueReference, ct);
        }

        return new MeetingProposal
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = meeting.WorkspaceId,
            SessionId = meeting.Id,
            SegmentId = segment.Id,
            IssueId = issueId,
            Candidates = candidates,
            Operation = action.Operation,
            Value = value,
            Before = issueId is { } id ? await CurrentAsync(db, id, action.Operation, ct) : null,
            Confidence = issueId is null ? Math.Min(action.Confidence, 0.5) : action.Confidence,
            Snippet = Truncate(action.Snippet.Length > 0 ? action.Snippet : segment.Text, 1000),
            Status = ProposalStatus.Pending,
            CreatedAt = clock.GetUtcNow(),
        };
    }

    /// <summary>An exact key wins; otherwise search the project. Several matches become candidates, never a guess.</summary>
    private async Task<(Guid? Id, List<string> Candidates)> ResolveIssueAsync(
        FlowstateDbContext db, string projectKey, string reference, CancellationToken ct)
    {
        var text = reference.Trim();
        if (IssueKey().IsMatch(text.ToUpperInvariant()))
        {
            var key = text.ToUpperInvariant();
            if (await db.Issues.Where(i => i.Key == key).Select(i => (Guid?)i.Id).SingleOrDefaultAsync(ct) is { } exact)
            {
                return (exact, []);
            }
        }

        var words = Article().Replace(text, "").Trim();
        if (words.Length < 2)
        {
            return (null, []);
        }

        var found = await issues.ListAsync(new IssueQuery
        {
            Q = words,
            Project = [projectKey],
            State = [IssueState.Backlog, IssueState.Todo, IssueState.InProgress, IssueState.InReview],
            Limit = MaxCandidates,
        }, ct);
        var items = found.Value?.Items ?? [];
        return items.Count == 1 ? (items[0].Id, []) : (null, items.Select(i => i.Key).ToList());
    }

    /// <summary>Canonical value for an operation, or null if it can't be resolved.</summary>
    private async Task<string?> NormalizeValueAsync(FlowstateDbContext db, MeetingSession meeting, ProposalOperation operation,
        string? raw, CancellationToken ct)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        switch (operation)
        {
            case ProposalOperation.State:
                return Enum.TryParse<IssueState>(value.Replace(" ", ""), ignoreCase: true, out var state) && Enum.IsDefined(state)
                    ? Camel(state)
                    : null;
            case ProposalOperation.Estimate:
                return value.Equals("none", StringComparison.OrdinalIgnoreCase) ? "none"
                    : decimal.TryParse(value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var hours) && hours is >= 0 and <= 100_000
                        ? hours.ToString(CultureInfo.InvariantCulture)
                        : null;
            case ProposalOperation.Comment:
                return Truncate(value, 4000);
            case ProposalOperation.CreateIssue:
                return Truncate(value, 500);
            case ProposalOperation.Sprint:
                if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    return "none";
                }

                var sprints = db.Sprints.Where(s => s.ProjectId == meeting.ProjectId && s.State != SprintState.Closed);
                var sprintId = value.ToLowerInvariant() switch
                {
                    "next" => await sprints.Where(s => s.State == SprintState.Planned).OrderBy(s => s.StartDate).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct),
                    "current" or "active" => await sprints.Where(s => s.State == SprintState.Active).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct),
                    _ when Guid.TryParse(value, out var id) => await sprints.Where(s => s.Id == id).Select(s => (Guid?)s.Id).SingleOrDefaultAsync(ct),
                    _ => await sprints.Where(s => EF.Functions.ILike(s.Name, value)).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct),
                };
                return sprintId?.ToString();
            case ProposalOperation.Assignee:
                if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    return "none";
                }

                if (value.Equals("me", StringComparison.OrdinalIgnoreCase) || value.Equals("mig", StringComparison.OrdinalIgnoreCase))
                {
                    return workspace.MemberId.ToString();
                }

                var people = await PeopleAsync(db, ct);
                if (Guid.TryParse(value, out var memberId))
                {
                    return people.Any(p => p.Id == memberId) ? memberId.ToString() : null;
                }

                // Full name first, then a unique first name ("give it to Anna").
                var exact = people.Where(p => p.Name.Equals(value, StringComparison.OrdinalIgnoreCase)).ToList();
                var first = people.Where(p => p.Name.Split(' ')[0].Equals(value.Split(' ')[0], StringComparison.OrdinalIgnoreCase)).ToList();
                return (exact.Count == 1 ? exact : first.Count == 1 ? first : []).SingleOrDefault()?.Id.ToString();
            default:
                return null;
        }
    }

    private static async Task<ExtractionContext> ContextAsync(FlowstateDbContext db, MeetingSession meeting, IReadOnlyList<string> recent,
        string utterance, CancellationToken ct)
    {
        var active = new[] { IssueState.Backlog, IssueState.Todo, IssueState.InProgress, IssueState.InReview };
        var hints = await db.Issues.AsNoTracking()
            .Where(i => i.ProjectId == meeting.ProjectId && active.Contains(i.State))
            .OrderByDescending(i => i.SprintId != null).ThenByDescending(i => i.UpdatedAt).Take(IssueHintLimit)
            .Select(i => new { i.Key, i.Title, i.State, i.AssigneeId })
            .ToListAsync(ct);
        var people = await PeopleAsync(db, ct);
        var sprints = await db.Sprints.AsNoTracking()
            .Where(s => s.ProjectId == meeting.ProjectId && s.State != SprintState.Closed)
            .OrderBy(s => s.StartDate).Select(s => s.Name).ToListAsync(ct);
        return new ExtractionContext(
            meeting.Language, meeting.Type, recent, utterance,
            hints.Select(h => new IssueHint(h.Key, h.Title, Camel(h.State), people.FirstOrDefault(p => p.Id == h.AssigneeId)?.Name)).ToList(),
            people.Where(p => p.Kind == MemberKind.Human).Select(p => p.Name).ToList(),
            sprints);
    }

    private static Task<List<MemberRef>> PeopleAsync(FlowstateDbContext db, CancellationToken ct) =>
        db.Members.AsNoTracking().Where(m => m.Status == MemberStatus.Active).Select(MemberRefs.Projection(db)).ToListAsync(ct);

    /// <summary>The field's current canonical value, for diffs and undo.</summary>
    private static async Task<string?> CurrentAsync(FlowstateDbContext db, Guid issueId, ProposalOperation operation, CancellationToken ct)
    {
        var issue = await db.Issues.AsNoTracking().Where(i => i.Id == issueId)
            .Select(i => new { i.State, i.SprintId, i.AssigneeId, i.Effort }).SingleOrDefaultAsync(ct);
        return issue is null ? null : operation switch
        {
            ProposalOperation.State => Camel(issue.State),
            ProposalOperation.Sprint => issue.SprintId?.ToString() ?? "none",
            ProposalOperation.Assignee => issue.AssigneeId?.ToString() ?? "none",
            ProposalOperation.Estimate => issue.Effort?.ToString(CultureInfo.InvariantCulture) ?? "none",
            _ => null,
        };
    }

    /// <summary>The <c>meeting-agent</c> member: reused if it exists, otherwise created for the person starting the meeting.</summary>
    private async Task<Guid?> AgentIdAsync(FlowstateDbContext db, CancellationToken ct)
    {
        var existing = await db.Members.Where(m => m.Kind == MemberKind.Agent && m.AgentName == AgentName)
            .Select(m => (Guid?)m.Id).SingleOrDefaultAsync(ct);
        if (existing is not null)
        {
            return existing;
        }

        await workspaces.EnsureAgentMemberAsync(workspace.Slug, workspace.UserId, AgentName, ct);
        return await db.Members.Where(m => m.Kind == MemberKind.Agent && m.AgentName == AgentName)
            .Select(m => (Guid?)m.Id).SingleOrDefaultAsync(ct);
    }

    private static async Task<MeetingSettings> SettingsAsync(FlowstateDbContext db, CancellationToken ct) =>
        await db.MeetingSettings.AsNoTracking().SingleOrDefaultAsync(ct) ?? NewSettings(Guid.Empty);

    private static MeetingSettings NewSettings(Guid workspaceId) => new()
    {
        WorkspaceId = workspaceId,
        DefaultMode = MeetingMode.Suggest,
        FullAutoOperations = [.. DefaultFullAutoOperations],
    };

    private async Task<List<MeetingResponse>> MeetingsAsync(FlowstateDbContext db, IQueryable<MeetingSession> query, CancellationToken ct)
    {
        var rows = await query.Select(m => new
        {
            Meeting = m,
            Project = db.Projects.Where(p => p.Id == m.ProjectId).Select(p => p.Key).First(),
            Counts = new MeetingCounts(
                    db.MeetingProposals.Count(p => p.SessionId == m.Id && p.Status == ProposalStatus.Pending),
                    db.MeetingProposals.Count(p => p.SessionId == m.Id && p.Status == ProposalStatus.Applied),
                    db.MeetingProposals.Count(p => p.SessionId == m.Id && p.Status == ProposalStatus.Rejected),
                    db.MeetingProposals.Count(p => p.SessionId == m.Id && p.Status == ProposalStatus.Undone)),
        })
            .ToListAsync(ct);
        var members = await MemberRefs.ByIdAsync(db, rows.SelectMany(r => new[] { r.Meeting.StartedById, r.Meeting.AgentId }), ct);
        return rows.Select(r => new MeetingResponse(
                r.Meeting.Id, r.Project, r.Meeting.Type, r.Meeting.Mode, r.Meeting.Language, r.Meeting.State,
                members[r.Meeting.StartedById], members[r.Meeting.AgentId], r.Meeting.StartedAt, r.Meeting.EndedAt, r.Counts,
                speech.Supports(r.Meeting.Language)))
            .ToList();
    }

    /// <summary>Responses with display values: issue refs, sprint and member names.</summary>
    private static async Task<List<ProposalResponse>> ProposalsAsync(FlowstateDbContext db, IReadOnlyList<MeetingProposal> proposals,
        CancellationToken ct)
    {
        var keys = proposals.SelectMany(p => p.Candidates).Distinct().ToList();
        var issueIds = proposals.Where(p => p.IssueId is not null).Select(p => p.IssueId!.Value).Distinct().ToList();
        var refs = await db.Issues.AsNoTracking().Where(i => issueIds.Contains(i.Id) || keys.Contains(i.Key))
            .Select(i => new { i.Id, Ref = new IssueRef(i.Key, i.Title, i.Type, i.State) }).ToListAsync(ct);
        var ids = proposals.SelectMany(p => new[] { p.Value, p.Before })
            .Select(v => Guid.TryParse(v, out var g) ? g : (Guid?)null).Where(g => g is not null).Select(g => g!.Value).ToList();
        var sprints = await db.Sprints.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var reviewerIds = proposals.Where(p => p.ReviewedById is not null).Select(p => p.ReviewedById!.Value);
        var memberIds = ids.Concat(reviewerIds).ToList();
        var members = await MemberRefs.ByIdAsync(db, memberIds, ct);

        string? Display(ProposalOperation op, string? value) => value switch
        {
            null => null,
            "none" => null,
            _ when op == ProposalOperation.Sprint && Guid.TryParse(value, out var id) => sprints.GetValueOrDefault(id),
            _ when op == ProposalOperation.Assignee && Guid.TryParse(value, out var id) => members.GetValueOrDefault(id)?.Name,
            _ => value,
        };

        return proposals.Select(p => new ProposalResponse(
                p.Id,
                p.SessionId,
                refs.FirstOrDefault(r => r.Id == p.IssueId)?.Ref,
                p.Candidates.Select(k => refs.FirstOrDefault(r => r.Ref.Key == k)?.Ref).OfType<IssueRef>().ToList(),
                p.Operation,
                p.Value,
                Display(p.Operation, p.Value),
                p.Before,
                Display(p.Operation, p.Before),
                p.Snippet,
                p.Confidence,
                p.Status,
                p.IssueId is null && p.Operation != ProposalOperation.CreateIssue,
                p.Edited,
                p.AutoApplied,
                p.ReviewedById is { } r ? members.GetValueOrDefault(r) : null,
                p.CreatedAt,
                p.AppliedAt))
            .ToList();
    }

    /// <summary>"flow 12" and "flow-12" in speech become FLOW-12.</summary>
    private static string NormalizeKeys(string text, string projectKey) =>
        Regex.Replace(text, $@"\b{Regex.Escape(projectKey)}[\s-]?(\d+)\b", m => $"{projectKey}-{m.Groups[1].Value}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string Camel<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static IssueError MeetingNotFound(Guid id) => new("meeting_not_found", $"No meeting {id}.");

    private static IssueError ProposalNotFound(Guid id) => new("proposal_not_found", $"No proposal {id} in this meeting.");

    private static IssueError NotLive() => new("meeting_not_live", "The meeting is paused or ended. Resume it to keep listening.");

    private static IssueError FullAutoDisabled() =>
        new("full_auto_disabled", "Full auto is off in this workspace. An admin can enable it in meeting settings.");

    [GeneratedRegex(@"^[A-Z][A-Z0-9]{1,9}-\d+$")]
    private static partial Regex IssueKey();

    [GeneratedRegex(@"^(?:the|den|det)\s+|\s+(?:issue|ticket|task|opgave|sag)$", RegexOptions.IgnoreCase)]
    private static partial Regex Article();

    [GeneratedRegex(@"\w+")]
    private static partial Regex Word();
}

/// <summary>Member display names: the agent name for agents, the user's display name for people.</summary>
internal static class MemberRefs
{
    public static Expression<Func<Member, MemberRef>> Projection(FlowstateDbContext db) =>
        m => new MemberRef(
            m.Id,
            m.Kind == MemberKind.Agent
                ? m.AgentName!
                : db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefault()!,
            m.Kind);

    public static async Task<Dictionary<Guid, MemberRef>> ByIdAsync(FlowstateDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Members.AsNoTracking().Where(m => list.Contains(m.Id)).Select(Projection(db)).ToDictionaryAsync(m => m.Id, ct);
    }
}
