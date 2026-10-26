using System.CommandLine;
using System.Globalization;
using Kupro.Cli.Api.Models;

namespace Kupro.Cli.Commands;

/// <summary>
/// meeting start|list|show|pause|resume|end|mode|say|proposals|approve|reject|edit|undo|settings (task 015). Commands
/// that act on a meeting take <c>--meeting</c>, defaulting to the most recent live one.
/// </summary>
internal static class MeetingCommands
{
    public static IEnumerable<Command> Build()
    {
        var meeting = new Command("meeting", "The meeting agent: it turns what is said into proposed issue changes.");
        foreach (var command in new[]
                 {
                     Start(), List(), Show(), State("pause", "Stop listening for now.", MeetingState.Paused),
                     State("resume", "Listen again.", MeetingState.Live), State("end", "End the meeting.", MeetingState.Ended),
                     Mode(), Say(), Proposals(), Review("approve", "Apply proposals (as the meeting agent).", ReviewDecision.Approve),
                     Review("reject", "Discard proposals.", ReviewDecision.Reject), Review("edit", "Change a proposal without applying it.", ReviewDecision.Edit),
                     Undo(), Settings(),
                 })
        {
            meeting.Subcommands.Add(command);
        }

        yield return meeting;
    }

    private static Command Start()
    {
        var project = new Option<string>("--project", "-p") { Description = "Project key.", Required = true };
        var type = Cli.EnumOption<MeetingType>("--type", "Kind of meeting");
        var mode = Cli.EnumOption<MeetingMode>("--mode", "Suggest waits for approval; full auto applies safe changes");
        var language = new Option<string>("--language", "-l") { Description = "Spoken language: en or da.", DefaultValueFactory = _ => "en" };
        language.AcceptOnlyFromAmong("en", "da");
        return new ApiCommand("start", "Start a meeting the agent listens to.", "startMeeting") { project, type, mode, language }.Handle(async (c, ct) =>
        {
            var m = await (await c.InWorkspaceAsync(ct)).Meetings.PostAsync(new StartMeetingRequest
            {
                Project = c.Get(project)!.ToUpperInvariant(),
                Type = c.Get(type) ?? MeetingType.Standup,
                Mode = c.Get(mode),
                Language = c.Get(language),
            }, cancellationToken: ct);
            await c.Out.DoneAsync(m!, $"Meeting {m!.Id} started in {Cli.Dashed(m.Mode.ToString()!)} mode. Send text with `meeting say`.");
        });
    }

    private static Command List()
    {
        var state = Cli.EnumOption<MeetingState>("--state", "Only meetings in this state");
        return new ApiCommand("list", "Recent meetings, newest first.", "listMeetings") { state }.Handle(async (c, ct) =>
        {
            var list = await (await c.InWorkspaceAsync(ct)).Meetings.GetAsync(r => r.QueryParameters.State = c.Get(state), ct) ?? [];
            await c.Out.ListAsync(list, ["ID", "PROJECT", "TYPE", "MODE", "STATE", "STARTED", "PENDING"], m =>
                [m.Id?.ToString(), m.Project, Cli.Dashed(m.Type.ToString()!), Cli.Dashed(m.Mode.ToString()!), Cli.Dashed(m.State.ToString()!),
                    Local(m.StartedAt), m.Counts?.Pending?.ToString(CultureInfo.InvariantCulture)]);
        });
    }

    private static Command Show()
    {
        var meeting = MeetingOption();
        return new ApiCommand("show", "A meeting's summary and transcript.", "getMeeting", "getMeetingTranscript") { meeting }.Handle(async (c, ct) =>
        {
            var api = (await c.InWorkspaceAsync(ct)).Meetings[await MeetingIdAsync(c, meeting, ct)];
            var m = (await api.GetAsync(cancellationToken: ct))!;
            var transcript = await api.Transcript.GetAsync(cancellationToken: ct) ?? [];
            await c.Out.ItemAsync(m, ("Project", m.Project), ("Mode", Cli.Dashed(m.Mode.ToString()!)), ("State", Cli.Dashed(m.State.ToString()!)),
                ("Started", $"{Local(m.StartedAt)} by {m.StartedBy?.Name}"),
                ("Proposals", $"{m.Counts?.Applied} applied, {m.Counts?.Rejected} rejected, {m.Counts?.Pending} pending, {m.Counts?.Undone} undone"),
                ("Transcript", string.Join(Environment.NewLine, transcript.Select(t => $"{t.Speaker?.Name}: {t.Text}"))));
        });
    }

    private static Command State(string name, string description, MeetingState state)
    {
        var meeting = MeetingOption();
        return new ApiCommand(name, description, "updateMeeting") { meeting }.Handle(async (c, ct) =>
        {
            var m = await (await c.InWorkspaceAsync(ct)).Meetings[await MeetingIdAsync(c, meeting, ct, live: state != MeetingState.Live)]
                .PatchAsync(new UpdateMeetingRequest { State = state }, cancellationToken: ct);
            await c.Out.DoneAsync(m!, $"Meeting {m!.Id} is {Cli.Dashed(m.State.ToString()!)}.");
        });
    }

    private static Command Mode()
    {
        var mode = Cli.EnumArgument<MeetingMode>("mode", "New mode");
        var meeting = MeetingOption();
        return new ApiCommand("mode", "Switch between suggest and full auto.", "updateMeeting") { mode, meeting }.Handle(async (c, ct) =>
        {
            var m = await (await c.InWorkspaceAsync(ct)).Meetings[await MeetingIdAsync(c, meeting, ct)]
                .PatchAsync(new UpdateMeetingRequest { Mode = c.Get(mode) }, cancellationToken: ct);
            await c.Out.DoneAsync(m!, $"Meeting {m!.Id} is in {Cli.Dashed(m.Mode.ToString()!)} mode.");
        });
    }

    private static Command Say()
    {
        var text = new Argument<string[]>("text") { Description = "What was said.", Arity = ArgumentArity.OneOrMore };
        var meeting = MeetingOption();
        return new ApiCommand("say", "Add what was said as text and show the proposals it produced.", "addMeetingTranscript") { text, meeting }
            .Handle(async (c, ct) =>
            {
                var result = await (await c.InWorkspaceAsync(ct)).Meetings[await MeetingIdAsync(c, meeting, ct)].Transcript
                    .PostAsync(new TranscriptRequest { Text = string.Join(' ', c.Get(text)!) }, cancellationToken: ct);
                await c.Out.ListAsync(result!.Proposals ?? [], ProposalHeaders, ProposalRow);
                if (result.BudgetSpent == true)
                {
                    await c.Out.MessageAsync("Meeting hours are used up this month: keyword matching made these proposals, so only clear commands count.");
                }
            });
    }

    private static Command Proposals()
    {
        var status = Cli.EnumOption<ProposalStatus>("--status", "Only proposals in this status");
        var meeting = MeetingOption();
        return new ApiCommand("proposals", "A meeting's proposals, oldest first.", "listMeetingProposals") { status, meeting }.Handle(async (c, ct) =>
        {
            var list = await (await c.InWorkspaceAsync(ct)).Meetings[await MeetingIdAsync(c, meeting, ct, live: false)].Proposals
                .GetAsync(r => r.QueryParameters.Status = c.Get(status), ct) ?? [];
            await c.Out.ListAsync(list, ProposalHeaders, ProposalRow);
        });
    }

    private static Command Review(string name, string description, ReviewDecision decision)
    {
        var ids = new Argument<Guid[]>("proposal") { Description = "Proposal ids (see `meeting proposals`).", Arity = ArgumentArity.OneOrMore };
        var value = new Option<string?>("--value") { Description = "Changed value: state, sprint id, member id, hours or text." };
        var issue = new Option<string?>("--issue") { Description = "Issue key, e.g. to pick one of the candidates." };
        var meeting = MeetingOption();
        var command = new ApiCommand(name, description, "reviewMeetingProposals") { ids, meeting };
        if (decision != ReviewDecision.Reject)
        {
            command.Options.Add(value);
            command.Options.Add(issue);
        }

        return command.Handle(async (c, ct) =>
        {
            var results = await (await c.InWorkspaceAsync(ct)).Meetings[await MeetingIdAsync(c, meeting, ct, live: false)].Proposals.Review
                .PostAsync(new ReviewProposalsRequest
                {
                    Items = c.Get(ids)!.Select(id => new ProposalReview
                    {
                        Id = id,
                        Decision = decision,
                        Value = decision == ReviewDecision.Reject ? null : c.Get(value),
                        Issue = decision == ReviewDecision.Reject ? null : c.Get(issue)?.ToUpperInvariant(),
                    }).ToList(),
                }, cancellationToken: ct) ?? [];
            await c.Out.ListAsync(results, [.. ProposalHeaders, "ERROR"], r => [.. ProposalRow(r.Proposal!), r.Error?.Detail]);
            if (results.Count(r => r.Error is not null) is > 0 and var failed)
            {
                throw new CliException($"{failed} of {results.Count} failed; see the ERROR column.");
            }
        });
    }

    private static Command Undo()
    {
        var id = new Argument<Guid?>("proposal") { Description = "Proposal to revert.", Arity = ArgumentArity.ZeroOrOne };
        var all = new Option<bool>("--all") { Description = "Revert everything the meeting applied." };
        var meeting = MeetingOption();
        return new ApiCommand("undo", "Revert an applied proposal, or the whole meeting with --all.", "undoMeetingProposal", "undoMeeting")
            { id, all, meeting }.Handle(async (c, ct) =>
        {
            var api = (await c.InWorkspaceAsync(ct)).Meetings[await MeetingIdAsync(c, meeting, ct, live: false)];
            if (c.Get(id) is { } proposalId)
            {
                var p = await api.Proposals[proposalId].Undo.PostAsync(cancellationToken: ct);
                await c.Out.DoneAsync(p!, $"Reverted {Change(p!)}.");
                return;
            }

            if (!c.Get(all))
            {
                throw new CliException("Name a proposal, or pass --all to revert the whole meeting.");
            }

            var results = await api.Undo.PostAsync(cancellationToken: ct) ?? [];
            await c.Out.ListAsync(results, [.. ProposalHeaders, "ERROR"], r => [.. ProposalRow(r.Proposal!), r.Error?.Detail]);
        });
    }

    private static Command Settings()
    {
        var defaultMode = Cli.EnumOption<MeetingMode>("--default-mode", "Mode new meetings start in");
        var fullAuto = new Option<bool?>("--full-auto") { Description = "Allow full auto in this workspace (owners only)." };
        var operations = Cli.EnumListOption<ProposalOperation>("--full-auto-op", "Operation full auto may apply");
        var retention = new Option<int?>("--retention-days") { Description = "Days to keep transcripts; 0 deletes them when the meeting ends." };
        return new ApiCommand("settings", "Show meeting settings and approval stats; options change them (owners).",
                "getMeetingSettings", "updateMeetingSettings") { defaultMode, fullAuto, operations, retention }
            .Handle(async (c, ct) =>
            {
                var api = (await c.InWorkspaceAsync(ct)).MeetingSettings;
                var ops = c.Get(operations) is { Length: > 0 } o ? o.Select(x => (ProposalOperation?)x).ToList() : null;
                var s = c.Get(defaultMode) is null && c.Get(fullAuto) is null && ops is null && c.Get(retention) is null
                    ? await api.GetAsync(cancellationToken: ct)
                    : await api.PatchAsync(new UpdateMeetingSettingsRequest
                    {
                        DefaultMode = c.Get(defaultMode),
                        FullAutoEnabled = c.Get(fullAuto),
                        FullAutoOperations = ops,
                        TranscriptRetentionDays = c.Get(retention),
                    }, cancellationToken: ct);
                await c.Out.ItemAsync(s!, ("Default mode", Cli.Dashed(s!.DefaultMode.ToString()!)),
                    ("Full auto", s.FullAutoEnabled == true ? $"on for {string.Join(", ", (s.FullAutoOperations ?? []).Select(x => Cli.Dashed(x.ToString()!)))}" : "off"),
                    ("Transcripts kept", s.TranscriptRetentionDays is 0 or null ? "until the meeting ends" : $"{s.TranscriptRetentionDays} days"),
                    ("Meeting hours", s.Usage is { } u
                        ? $"{u.HoursUsed?.ToString("0.#", CultureInfo.InvariantCulture)} of {u.HoursIncluded?.ToString("0.#", CultureInfo.InvariantCulture)} used, resets {u.ResetsOn}{(u.Spent == true ? " (used up: microphone off, keyword proposals only)" : "")}"
                        : "no limit"),
                    ("Approved unchanged", string.Join(", ", (s.Stats ?? []).Where(x => x.Decided > 0)
                        .Select(x => $"{Cli.Dashed(x.Operation.ToString()!)} {x.ApprovedUnchanged}/{x.Decided}"))));
            });
    }

    private static readonly string[] ProposalHeaders = ["ID", "ISSUE", "CHANGE", "CONFIDENCE", "STATUS", "SAID"];

    private static string?[] ProposalRow(ProposalResponse p) =>
    [
        p.Id?.ToString(),
        p.Issue?.Key ?? (p.NeedsIssue == true ? $"? {string.Join("/", (p.Candidates ?? []).Select(x => x.Key))}" : "new"),
        Change(p),
        p.Confidence?.ToString("0.00", CultureInfo.InvariantCulture),
        Cli.Dashed(p.Status.ToString()!),
        p.Snippet,
    ];

    private static string Change(ProposalResponse p) => p.Operation switch
    {
        ProposalOperation.Comment => $"comment: {p.ValueDisplay}",
        ProposalOperation.CreateIssue => $"create: {p.ValueDisplay}",
        _ => $"{Cli.Dashed(p.Operation.ToString()!)}: {p.BeforeDisplay ?? "-"} -> {p.ValueDisplay ?? "-"}",
    };

    private static Option<Guid?> MeetingOption() =>
        new("--meeting", "-m") { Description = "Meeting id. Default: the most recent live meeting." };

    private static async Task<Guid> MeetingIdAsync(CommandContext c, Option<Guid?> option, CancellationToken ct, bool live = true)
    {
        if (c.Get(option) is { } id)
        {
            return id;
        }

        var meetings = await (await c.InWorkspaceAsync(ct)).Meetings.GetAsync(r => r.QueryParameters.State = live ? MeetingState.Live : null, ct) ?? [];
        return meetings.FirstOrDefault()?.Id ?? throw new CliException(live ? "No live meeting. Start one with `meeting start`." : "No meetings yet.");
    }

    private static string? Local(DateTimeOffset? at) => at?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
