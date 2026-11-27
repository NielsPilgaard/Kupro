using System.CommandLine;
using System.Globalization;
using Kupro.Cli.Api.Models;

namespace Kupro.Cli.Commands;

/// <summary>
/// say (task 036): voice control's text path. Reads what you'd say, shows the proposed changes and applies them through
/// the issue endpoints after you confirm, as the web's confirmation card does. Navigation and audio are web-only.
/// </summary>
internal static class VoiceCommands
{
    public static IEnumerable<Command> Build()
    {
        var text = new Argument<string[]>("text") { Description = "What you'd say, e.g. \"move KUP-12 to next sprint\".", Arity = ArgumentArity.OneOrMore };
        var yes = new Option<bool>("--yes", "-y") { Description = "Apply without asking. Changes that need an issue picked are still skipped." };
        var issue = new Option<string?>("--issue") { Description = "Issue key that \"this one\" means." };
        var project = new Option<string?>("--project", "-p") { Description = "Project key for \"next sprint\" and new issues." };
        var language = new Option<string?>("--language", "-l") { Description = "Language: en or da (default: your profile's)." };
        language.AcceptOnlyFromAmong("en", "da");
        yield return new ApiCommand("say", "Tell Kupro what to change in plain words; shows the changes and applies them after you confirm.",
            "interpretVoice") { text, yes, issue, project, language }.Handle(async (c, ct) =>
        {
            var api = await c.InWorkspaceAsync(ct);
            var reading = (await api.Voice.Interpret.PostAsync(new VoiceInterpretRequest
            {
                Text = string.Join(' ', c.Get(text)!),
                Issue = c.Get(issue)?.ToUpperInvariant(),
                Project = c.Get(project)?.ToUpperInvariant(),
                Language = c.Get(language),
            }, cancellationToken: ct))!;

            if (reading.Kind != VoiceKind.Operations)
            {
                await c.Out.DoneAsync(reading, reading.Kind == VoiceKind.Navigate ? Place(reading.Navigate!) : Nothing(reading.Reason));
                return;
            }

            var operations = reading.Operations ?? [];
            await c.Out.ListAsync(operations, ["ISSUE", "CHANGE", "FROM", "TO", "NOTE"], o =>
                [o.Issue?.Key ?? (o.Operation == ProposalOperation.CreateIssue ? "new" : "?"), Cli.Dashed(o.Operation.ToString()!),
                    o.BeforeDisplay, o.ValueDisplay ?? o.Value, Note(o)]);
            if (reading.BudgetSpent == true)
            {
                await c.Out.MessageAsync("AI hours are used up this month: keyword matching read this, so only clear commands count.");
            }

            var ready = operations.Where(o => o.NeedsIssue != true).ToList();
            if (ready.Count == 0)
            {
                throw new CliException("Nothing to apply: name the issue by its key, e.g. KUP-12.");
            }

            if (!c.Get(yes) && !await ConfirmAsync(c, ready.Count, ct))
            {
                await c.Out.MessageAsync("Nothing changed.");
                return;
            }

            foreach (var o in ready)
            {
                await ApplyAsync(api, o, ct);
            }

            await c.Out.MessageAsync(ready.Count == 1 ? "Applied." : $"Applied {ready.Count} changes.");
        });
    }

    /// <summary>Through the same issue endpoints the web uses; the proposal carries each body.</summary>
    private static async Task ApplyAsync(Api.Api.V1.Workspaces.Item.WithWorkspaceItemRequestBuilder api, VoiceOperation o, CancellationToken ct)
    {
        switch (o.Operation)
        {
            case ProposalOperation.Comment:
                await api.Issues[o.Issue!.Key!].Comments.PostAsync(new CommentRequest { Body = o.Value }, cancellationToken: ct);
                break;
            case ProposalOperation.CreateIssue:
                await api.Issues.PostAsync(o.Create!, cancellationToken: ct);
                break;
            default:
                await api.Issues[o.Issue!.Key!].PatchAsync(o.Update!, cancellationToken: ct);
                break;
        }
    }

    private static async Task<bool> ConfirmAsync(CommandContext c, int count, CancellationToken ct)
    {
        var input = Cli.Runtime.Value is { } runtime ? runtime.In : Console.IsInputRedirected ? null : Console.In;
        if (input is null || c.Out.Json)
        {
            throw new CliException("Pass --yes to apply without being asked.");
        }

        await c.Out.Writer.WriteAsync((count == 1 ? "Apply this change? [y/N] " : $"Apply these {count} changes? [y/N] ").AsMemory(), ct);
        var answer = (await input.ReadLineAsync(ct))?.Trim().ToLowerInvariant();
        return answer is "y" or "yes" or "j" or "ja";
    }

    private static string? Note(VoiceOperation o)
    {
        if (o.NeedsIssue == true)
        {
            return o.Candidates is { Count: > 0 } candidates
                ? "which one? " + string.Join(", ", candidates.Select(i => i.Key)) + " (skipped)"
                : "no issue matched (skipped)";
        }

        return o.Confidence < 0.8 ? $"unsure ({(o.Confidence ?? 0).ToString("0%", CultureInfo.InvariantCulture)})" : null;
    }

    private static string Place(VoiceTarget target) => target.View switch
    {
        VoiceView.Issue => $"That opens {target.Issue} in the web app. Here: `kupro issue show {target.Issue}`.",
        _ => "That opens a view in the web app; the CLI only applies changes.",
    };

    private static string Nothing(VoiceReason? reason) => reason switch
    {
        VoiceReason.NothingHeard => "Nothing to read.",
        VoiceReason.NotFound => "Couldn't find that issue, person or sprint in this workspace.",
        VoiceReason.ProjectNeeded => "Say which project, or pass --project.",
        _ => "Didn't catch a change. Try e.g. \"move KUP-12 to next sprint\" or \"assign KUP-3 to Anna\".",
    };
}
