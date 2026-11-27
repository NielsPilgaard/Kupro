using System.Net;
using System.Text.RegularExpressions;
using Kupro.Api.Data;
using Kupro.Api.Features.Ai;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Meetings;
using Kupro.Api.Features.Sprints;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Polly.Timeout;

namespace Kupro.Api.Features.Voice;

/// <summary>
/// Voice control (task 036): reads one push-to-talk utterance into proposed changes, a place to open, or nothing, and
/// transcribes dictation. Changes nothing itself: clients apply proposals through the issue endpoints, as the person.
/// Issue references are matched by our own search and every place is checked against this workspace, so a target the
/// person can't see is never produced. Audio is kept in memory for the call and dropped (ADR-015 IMP-004).
/// </summary>
public sealed partial class VoiceService(
    IDbContextFactory<KuproDbContext> dbFactory,
    WorkspaceContext workspace,
    IssueService issues,
    IVoiceInterpreter interpreter,
    ISpeechToText speech,
    AiRouter router,
    AiConnectionService connections,
    MeetingBudget budget,
    ILogger<VoiceService> logger)
{
    /// <summary>Below this, a change waits for a tap even with "apply without asking" on (as full auto, task 015).</summary>
    public const double SureConfidence = MeetingService.AutoApplyConfidence;

    private const int IssueHintLimit = 60;

    /// <summary>Tidy-up may fix a word's spelling, not swap it: at most this share of a word's letters may change.</summary>
    private const double MaxSpellingChange = 0.34;

    private static readonly KeywordVoiceInterpreter Keywords = new();

    /// <summary>Reads typed or already transcribed text: the CLI's <c>kupro say</c>, tests, and the web after transcription.</summary>
    public async Task<IssueResult<VoiceInterpretation>> InterpretAsync(VoiceInterpretRequest request, CancellationToken ct) =>
        await ReadAsync(request.Text.Trim(), request.Issue, request.Project, request.Language ?? await LanguageAsync(ct), ct);

    /// <summary>Transcribes one utterance (WAV) and reads it. Browser only.</summary>
    public async Task<IssueResult<VoiceInterpretation>> InterpretAudioAsync(Stream wav, VoiceAudioQuery query, CancellationToken ct)
    {
        var language = query.Language ?? await LanguageAsync(ct);
        var heard = await TranscribeAsync(wav, language, ct);
        if (heard.Error is { } error)
        {
            return error;
        }

        return string.IsNullOrWhiteSpace(heard.Value)
            ? None("", VoiceReason.NothingHeard, ExtractorKind.Keyword, spent: false)
            : await ReadAsync(heard.Value.Trim(), query.Issue, query.Project, language, ct);
    }

    /// <summary>Dictation: the words as said, and with <c>tidy</c> a suggestion that only fixes spelling and punctuation.</summary>
    public async Task<IssueResult<VoiceTranscription>> TranscribeAsync(Stream wav, VoiceTranscribeQuery query, CancellationToken ct)
    {
        var language = query.Language ?? await LanguageAsync(ct);
        var heard = await TranscribeAsync(wav, language, ct);
        if (heard.Error is { } error)
        {
            return error;
        }

        var text = heard.Value!.Trim();
        var tidied = query.Tidy && text.Length > 0 ? await TidyAsync(text, language, ct) : null;
        return new VoiceTranscription(text, tidied is not null && tidied != text ? tidied : null);
    }

    // ---------------------------------------------------------------- audio

    private async Task<IssueResult<string>> TranscribeAsync(Stream wav, string language, CancellationToken ct)
    {
        if (await router.ResolveAsync(AiCapability.Transcription, language, ct) is not { } route)
        {
            return new IssueError("transcription_unavailable", $"No speech-to-text model is configured for '{language}'.");
        }

        // A workspace's own key that its vendor refused stops the microphone; Kupro's key is never used instead (ADR-022).
        if (route.Rejected)
        {
            return ConnectionRejected();
        }

        // Speech has no free fallback, so a spent budget stops the microphone (ADR-013); typed text still works.
        if (!route.OwnKey && await budget.CheckAsync(ct) != BudgetState.Available)
        {
            return new IssueError("ai_budget_spent",
                "This workspace's AI hours are used up for this month, so the microphone is off until next month. Typing still works.");
        }

        Transcription transcription;
        try
        {
            transcription = await speech.TranscribeAsync(wav, language, route.Route, ct);
        }
        catch (HttpRequestException ex) when (route.OwnKey && Refused(ex))
        {
            await connections.RejectAsync(route.ConnectionId!.Value, $"{(int)ex.StatusCode!.Value} {ex.StatusCode}", ct);
            return ConnectionRejected();
        }

        await budget.RecordAudioAsync(transcription.AudioSeconds, route, ct, AiFeature.Voice);
        return transcription.Text;
    }

    /// <summary>Null when no model can tidy (none set up, refused, over budget or failing) or its answer changed words.</summary>
    private async Task<string?> TidyAsync(string text, string language, CancellationToken ct)
    {
        if (await router.ResolveAsync(AiCapability.VoiceCommands, language, ct) is not { Rejected: false } route
            || (!route.OwnKey && await budget.CheckAsync(ct) != BudgetState.Available))
        {
            return null;
        }

        try
        {
            var result = await interpreter.TidyAsync(text, language, route.Route, ct);
            if (result.Usage is { } usage)
            {
                await budget.RecordTokensAsync(usage, route, ct, AiFeature.Voice);
            }

            return OnlySpellingAndPunctuation(text, result.Text) ? result.Text : null;
        }
        catch (HttpRequestException ex) when (route.OwnKey && Refused(ex))
        {
            await connections.RejectAsync(route.ConnectionId!.Value, $"{(int)ex.StatusCode!.Value} {ex.StatusCode}", ct);
            return null;
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            LogModelFailed(logger, ex);
            return null;
        }
    }

    /// <summary>
    /// The person's words are never rewritten: the same words in the same order, each spelled at most a little
    /// differently. Case, punctuation and spacing may change.
    /// </summary>
    internal static bool OnlySpellingAndPunctuation(string original, string tidied)
    {
        static List<string> Words(string s) => Word().Matches(s).Select(m => m.Value.ToLowerInvariant()).ToList();

        var before = Words(original);
        var after = Words(tidied);
        return before.Count == after.Count && before.Zip(after).All(w =>
            w.First == w.Second || Distance(w.First, w.Second) <= Math.Max(1, (int)(Math.Max(w.First.Length, w.Second.Length) * MaxSpellingChange)));
    }

    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }

        return row[b.Length];
    }

    // ---------------------------------------------------------------- reading

    private async Task<IssueResult<VoiceInterpretation>> ReadAsync(string text, string? issueKey, string? projectKey, string language,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var projects = await db.Projects.AsNoTracking().OrderBy(p => p.Key).Select(p => new { p.Id, p.Key, p.Name }).ToListAsync(ct);
        foreach (var p in projects)
        {
            text = ProposalValues.NormalizeKeys(text, p.Key);
        }

        if (text.Length == 0)
        {
            return None(text, VoiceReason.NothingHeard, ExtractorKind.Keyword, spent: false);
        }

        // The screen: a key that isn't in this workspace counts as no issue on screen.
        var onScreen = issueKey?.Trim().ToUpperInvariant();
        var current = onScreen is null ? null : await db.Issues.AsNoTracking().Where(i => i.Key == onScreen)
            .Select(i => new { i.Id, i.Key, i.Title, i.State, i.AssigneeId, i.ProjectId }).SingleOrDefaultAsync(ct);
        var project = projects.FirstOrDefault(p => p.Id == current?.ProjectId)
                      ?? projects.FirstOrDefault(p => p.Key.Equals(projectKey?.Trim(), StringComparison.OrdinalIgnoreCase));
        var people = await ProposalValues.PeopleAsync(db, ct);
        var context = await ContextAsync(db, language, projects.Select(p => $"{p.Key}: {p.Name}").ToList(), people, project?.Id, project?.Key,
            current is null ? null : new IssueHint(current.Key, current.Title, ProposalValues.Camel(current.State),
                people.FirstOrDefault(p => p.Id == current.AssigneeId)?.Name), text, ct);

        var (reading, extractor, spent) = await ReadModelAsync(context, ct);

        var operations = new List<VoiceOperation>();
        foreach (var action in reading.Actions)
        {
            if (await OperationAsync(db, action, current?.Id, project?.Id, project?.Key, ct) is { } operation)
            {
                operations.Add(operation);
            }
        }

        if (operations.Count > 0)
        {
            return new VoiceInterpretation(VoiceKind.Operations, text, operations, null, null, extractor, spent);
        }

        if (reading.Kind == VoiceKind.Navigate || reading.Navigate is not null)
        {
            var (target, reason) = await TargetAsync(db, reading.Navigate, current?.Key, project?.Id, project?.Key,
                projects.Select(p => (p.Id, p.Key, p.Name)).ToList(), people, ct);
            return target is not null
                ? new VoiceInterpretation(VoiceKind.Navigate, text, [], target, null, extractor, spent)
                : None(text, reason, extractor, spent);
        }

        // Heard a change, but its issue, person or sprint isn't here.
        return None(text, reading.Actions.Count > 0 ? VoiceReason.NotFound : VoiceReason.NotUnderstood, extractor, spent);
    }

    /// <summary>
    /// The model when one serves <c>voice-commands</c>, otherwise keyword matching: no model set up, the workspace's own
    /// key refused (never Kupro's key instead, ADR-022), Kupro's budget spent (ADR-013), or the vendor failing (ADR-015 IMP-007).
    /// </summary>
    private async Task<(VoiceReading Reading, ExtractorKind Kind, bool Spent)> ReadModelAsync(VoiceContext context, CancellationToken ct)
    {
        if (await router.ResolveAsync(AiCapability.VoiceCommands, context.Language, ct) is not { Rejected: false } route)
        {
            return (await Keywords.InterpretAsync(context, ct), ExtractorKind.Keyword, false);
        }

        if (!route.OwnKey && await budget.CheckAsync(ct) != BudgetState.Available)
        {
            return (await Keywords.InterpretAsync(context, ct), ExtractorKind.Keyword, true);
        }

        try
        {
            var reading = await interpreter.InterpretAsync(context, route.Route, ct);
            if (reading.Usage is { } usage)
            {
                await budget.RecordTokensAsync(usage, route, ct, AiFeature.Voice);
            }

            return (reading, reading.Usage is null ? ExtractorKind.Keyword : ExtractorKind.Model, false);
        }
        catch (HttpRequestException ex) when (route.OwnKey && Refused(ex))
        {
            await connections.RejectAsync(route.ConnectionId!.Value, $"{(int)ex.StatusCode!.Value} {ex.StatusCode}", ct);
            return (await Keywords.InterpretAsync(context, ct), ExtractorKind.Keyword, false);
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            LogModelFailed(logger, ex);
            return (await Keywords.InterpretAsync(context, ct), ExtractorKind.Keyword, false);
        }
    }

    /// <summary>One heard change as a proposal with its diff, or null when its value can't be used here.</summary>
    private async Task<VoiceOperation?> OperationAsync(KuproDbContext db, ExtractedAction action, Guid? currentId, Guid? projectId,
        string? projectKey, CancellationToken ct)
    {
        Guid? issueId = null;
        List<string> candidates = [];
        if (action.Operation != ProposalOperation.CreateIssue)
        {
            (issueId, candidates) = string.IsNullOrWhiteSpace(action.IssueReference) || ThisOne().IsMatch(action.IssueReference.Trim())
                ? (currentId, [])
                : await ProposalValues.ResolveIssueAsync(db, issues, projectKey, action.IssueReference, ct);
            if (issueId is null && candidates.Count == 0)
            {
                return null;
            }
        }
        else if (projectKey is null)
        {
            return null;
        }

        // "Next sprint" is the target issue's project's next sprint.
        var issueProject = issueId is { } target
            ? await db.Issues.Where(i => i.Id == target).Select(i => (Guid?)i.ProjectId).SingleAsync(ct)
            : projectId;
        if (await ProposalValues.NormalizeAsync(db, issueProject, workspace.MemberId, action.Operation, action.Value, ct) is not { } value)
        {
            return null;
        }

        var before = issueId is { } id ? await ProposalValues.CurrentAsync(db, id, action.Operation, ct) : null;
        var confidence = issueId is null && action.Operation != ProposalOperation.CreateIssue ? Math.Min(action.Confidence, 0.5) : action.Confidence;
        var held = new List<VoiceHold>();
        if (confidence < SureConfidence)
        {
            held.Add(VoiceHold.LowConfidence);
        }

        if (issueId is null && action.Operation != ProposalOperation.CreateIssue)
        {
            held.Add(VoiceHold.Ambiguous);
        }

        if (action.Operation == ProposalOperation.State && value == ProposalValues.Camel(IssueState.Canceled))
        {
            held.Add(VoiceHold.Destructive);
        }

        if (action.Operation == ProposalOperation.CreateIssue)
        {
            held.Add(VoiceHold.CreateIssue);
        }

        var isField = action.Operation is not (ProposalOperation.Comment or ProposalOperation.CreateIssue);
        var refs = await db.Issues.AsNoTracking().Where(i => i.Id == issueId || candidates.Contains(i.Key))
            .Select(i => new { i.Id, Ref = new IssueRef(i.Key, i.Title, i.Type, i.State) }).ToListAsync(ct);
        return new VoiceOperation(
            refs.FirstOrDefault(r => r.Id == issueId)?.Ref,
            candidates.Select(k => refs.FirstOrDefault(r => r.Ref.Key == k)?.Ref).OfType<IssueRef>().ToList(),
            action.Operation,
            value,
            await DisplayAsync(db, action.Operation, value, ct),
            before,
            await DisplayAsync(db, action.Operation, before, ct),
            ProposalValues.Truncate(action.Snippet, 1000),
            confidence,
            issueId is null && action.Operation != ProposalOperation.CreateIssue,
            held,
            isField ? ProposalValues.Change(action.Operation, value) : null,
            isField && before is not null ? ProposalValues.Change(action.Operation, before) : null,
            action.Operation == ProposalOperation.CreateIssue ? new CreateIssueRequest(projectKey!, value, State: IssueState.Todo) : null);
    }

    /// <summary>A heard place checked against this workspace. Anything not found here is never returned.</summary>
    private static async Task<(VoiceTarget? Target, VoiceReason Reason)> TargetAsync(KuproDbContext db, HeardTarget? heard,
        string? currentKey, Guid? projectId, string? projectKey, IReadOnlyList<(Guid Id, string Key, string Name)> projects,
        IReadOnlyList<MemberRef> people, CancellationToken ct)
    {
        if (heard is null)
        {
            return (null, VoiceReason.NotUnderstood);
        }

        // A project that was said wins over the one on screen; one that was said and isn't here is not found.
        var (pid, pkey) = (projectId, projectKey);
        if (heard.Project.Length > 0)
        {
            var said = projects.Where(p => p.Key.Equals(heard.Project, StringComparison.OrdinalIgnoreCase)
                    || p.Name.Equals(heard.Project, StringComparison.OrdinalIgnoreCase))
                .Select(p => ((Guid?)p.Id, (string?)p.Key)).FirstOrDefault();
            if (said.Item1 is null)
            {
                return (null, VoiceReason.NotFound);
            }

            (pid, pkey) = said;
        }
        MemberRef? assignee = null;
        if (heard.Assignee.Length > 0 && (assignee = ProposalValues.MatchPerson(people, heard.Assignee)) is null)
        {
            return (null, VoiceReason.NotFound);
        }

        switch (heard.View)
        {
            case VoiceView.Issue:
                var key = heard.Issue.Length == 0 || ThisOne().IsMatch(heard.Issue) ? currentKey : heard.Issue.ToUpperInvariant();
                var found = key is not null && await db.Issues.AnyAsync(i => i.Key == key, ct);
                return found ? (new VoiceTarget(VoiceView.Issue, key, null, null, null, null), default) : (null, VoiceReason.NotFound);
            case VoiceView.MyIssues:
                return (new VoiceTarget(VoiceView.MyIssues, null, null, null, null, null), default);
            case VoiceView.List or VoiceView.Board:
                return pkey is null
                    ? (null, VoiceReason.ProjectNeeded)
                    : (new VoiceTarget(heard.View, null, pkey, assignee, null, null), default);
            case VoiceView.Sprints:
                var sprint = pid is { } p && heard.Sprint.Length > 0 && await ProposalValues.NormalizeAsync(db, p, Guid.Empty,
                    ProposalOperation.Sprint, heard.Sprint, ct) is { } s && Guid.TryParse(s, out var sprintId)
                    ? await db.Sprints.AsNoTracking().Where(x => x.Id == sprintId).Select(x => new { x.Id, x.Name }).SingleOrDefaultAsync(ct)
                    : null;
                return (new VoiceTarget(VoiceView.Sprints, null, pkey, null, sprint?.Id, sprint?.Name), default);
            case VoiceView.Plan:
                return (new VoiceTarget(VoiceView.Plan, null, pkey, null, null, null), default);
            default:
                return (null, VoiceReason.NotUnderstood);
        }
    }

    private static async Task<VoiceContext> ContextAsync(KuproDbContext db, string language, IReadOnlyList<string> projects,
        IReadOnlyList<MemberRef> people, Guid? projectId, string? projectKey, IssueHint? current, string utterance, CancellationToken ct)
    {
        var active = new[] { IssueState.Backlog, IssueState.Todo, IssueState.InProgress, IssueState.InReview };
        var hints = await db.Issues.AsNoTracking()
            .Where(i => (projectId == null || i.ProjectId == projectId) && active.Contains(i.State))
            .OrderByDescending(i => i.SprintId != null).ThenByDescending(i => i.UpdatedAt).Take(IssueHintLimit)
            .Select(i => new { i.Key, i.Title, i.State, i.AssigneeId })
            .ToListAsync(ct);
        var sprints = projectId is null ? [] : await db.Sprints.AsNoTracking()
            .Where(s => s.ProjectId == projectId && s.State != SprintState.Closed)
            .OrderBy(s => s.StartDate).Select(s => s.Name).ToListAsync(ct);
        return new VoiceContext(
            language,
            projects,
            people.Where(p => p.Kind == MemberKind.Human).Select(p => p.Name).ToList(),
            sprints,
            hints.OrderBy(h => h.Key, StringComparer.Ordinal)
                .Select(h => new IssueHint(h.Key, h.Title, ProposalValues.Camel(h.State), people.FirstOrDefault(p => p.Id == h.AssigneeId)?.Name))
                .ToList(),
            projectKey,
            current,
            utterance);
    }

    /// <summary>A value as people read it: sprint and member names; null for "none".</summary>
    private static async Task<string?> DisplayAsync(KuproDbContext db, ProposalOperation operation, string? value, CancellationToken ct)
    {
        if (value is null or "none")
        {
            return null;
        }

        if (!Guid.TryParse(value, out var id))
        {
            return value;
        }

        return operation switch
        {
            ProposalOperation.Sprint => await db.Sprints.Where(s => s.Id == id).Select(s => s.Name).SingleOrDefaultAsync(ct),
            ProposalOperation.Assignee => (await MemberRefs.ByIdAsync(db, [id], ct)).GetValueOrDefault(id)?.Name,
            _ => value,
        };
    }

    private async Task<string> LanguageAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var locale = await db.Users.Where(u => u.Id == workspace.UserId).Select(u => u.Locale).SingleOrDefaultAsync(ct);
        return locale == "da" ? "da" : "en";
    }

    private static VoiceInterpretation None(string text, VoiceReason reason, ExtractorKind extractor, bool spent) =>
        new(VoiceKind.None, text, [], null, reason, extractor, spent);

    private static bool Refused(HttpRequestException ex) => ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static bool IsTransient(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or TimeoutRejectedException || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private static IssueError ConnectionRejected() => new("ai_connection_rejected",
        "The workspace's own AI key was rejected by its provider, so the microphone is off. Replace the key in Settings → AI, or remove the connection to use Kupro's included hours. Typing still works.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Voice model call failed; fell back to keyword matching")]
    private static partial void LogModelFailed(ILogger logger, Exception exception);

    /// <summary>"this one", "it", "den her": the issue on screen.</summary>
    [GeneratedRegex(@"^(?:this(?:\s+(?:one|issue|ticket|task))?|it|den\s+her|det\s+her|denne(?:\s+(?:sag|opgave))?)$", RegexOptions.IgnoreCase)]
    private static partial Regex ThisOne();

    [GeneratedRegex(@"\w+")]
    private static partial Regex Word();
}
