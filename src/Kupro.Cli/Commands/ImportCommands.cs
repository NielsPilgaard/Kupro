using System.CommandLine;
using System.Globalization;
using Kupro.Cli.Api.Models;
using Kupro.Import;
using Kupro.Import.AzureDevOps;
using Kupro.Import.GitHub;
using Kupro.Import.Jira;
using Kupro.Import.Linear;
using Kupro.Import.Monday;
using Kupro.Import.Trello;
using Kupro.Import.YouTrack;
using Microsoft.Kiota.Abstractions.Serialization;
using ReadScope = Kupro.Import.Snapshot.ReadScope;
using SnapshotDescription = Kupro.Import.Snapshot.SourceDescription;
using SourceKinds = Kupro.Import.Snapshot.SourceKinds;

namespace Kupro.Cli.Commands;

/// <summary>
/// kupro import ... (task 023, ADR-019): every step of an import except the interactive People step, which is a mapping
/// file here (<c>import mapping</c> prints the proposed one). Source tokens come from stdin or KUPRO_IMPORT_TOKEN, never
/// an argument, so they stay out of shell history.
/// </summary>
internal static class ImportCommands
{
    private const string TokenVariable = "KUPRO_IMPORT_TOKEN";

    public static IEnumerable<Command> Build()
    {
        var import = new Command("import", "Bring issues over from another tracker, a CSV, a snapshot or a Kupro export (owners).");
        foreach (var command in new[]
                 {
                     FromFile("csv", "csv", "Import a CSV file: prints the columns Kupro found and how it maps them."),
                     FromFile("snapshot", "snapshot", "Import a snapshot ZIP (docs/import-format.md), e.g. from a connector's --local mode."),
                     FromFile("kupro", "kupro", "Move a workspace here from a Kupro export ZIP."),
                     FromSource(SourceKinds.Jira, "Jira Cloud or Data Center", "--url: the site (https://acme.atlassian.net) or server address. Token: email:api-token on Cloud, a personal access token on Data Center."),
                     FromSource(SourceKinds.AzureDevOps, "Azure DevOps Services or Server", "--url: the organisation or collection address. Token: a personal access token with Work Items (Read)."),
                     FromSource(SourceKinds.GitHub, "GitHub Issues", "--url: only for GitHub Enterprise Server. Token: a fine-grained token with Issues and Metadata (read)."),
                     FromSource(SourceKinds.YouTrack, "YouTrack Cloud or self-hosted", "--url: the address you open (https://acme.youtrack.cloud). Token: a permanent token."),
                     FromSource(SourceKinds.Monday, "Monday", "Token: a personal API token."),
                     FromSource(SourceKinds.Linear, "Linear", "Token: a personal API key."),
                     FromSource(SourceKinds.Trello, "Trello", "Token: a Trello token; --key: your Trello API key."),
                     Create(), List(), Show(), Upload(), Source(), Mapping(), Plan(), Run(), Runs(), Report(), Sync(), Token(), Conflicts(), Undo(), Finish(),
                 })
        {
            import.Subcommands.Add(command);
        }

        yield return import;
    }

    private static Argument<Guid> Id() => new("id") { Description = "Import id (see `import list`)." };

    private static Command FromFile(string name, string kind, string description)
    {
        var file = new Argument<FileInfo>("file") { Description = "The file to import." };
        return new ApiCommand(name, description, "createImport", "uploadImportSource") { file }.Handle(async (c, ct) =>
        {
            var api = await c.InWorkspaceAsync(ct);
            var created = await api.Imports.PostAsync(new CreateImportRequest { SourceKind = kind }, cancellationToken: ct);
            var upload = await UploadAsync(c, created!.Id!.Value, c.Get(file)!, ct);
            await Uploaded(c, created.Id.Value, upload);
        });
    }

    /// <summary>
    /// <c>kupro import jira --url ...</c>: without --local, the same as <c>import create</c>. With --local the connector
    /// runs here (a server behind a VPN, or a token that must not leave this machine) and uploads a snapshot ZIP with
    /// the attachment bytes in it, so the token never reaches Kupro (ADR-019 Source access).
    /// </summary>
    private static Command FromSource(string kind, string name, string access)
    {
        var url = new Option<string?>("--url") { Description = "The source's address." };
        var key = new Option<string?>("--key") { Description = "Your Trello API key (--local only; a key is not a secret, the token is)." };
        var local = new Option<bool>("--local") { Description = "Read the source from this machine and upload a snapshot. The token stays here." };
        var projects = new Option<string[]>("--project") { Description = "With --local: a project key or ID to read (repeat it); all projects if left out.", AllowMultipleArgumentsPerToken = true };
        var into = new Option<Guid?>("--import") { Description = "With --local: upload to this import (a re-run) instead of creating one." };
        var output = new Option<FileInfo?>("--out") { Description = "With --local: write the snapshot ZIP here and stop, to look at it before `kupro import snapshot`." };
        var command = new ApiCommand(kind, $"Import from {name}. The token comes from stdin or {TokenVariable}. {access}", "createImport", "uploadImportSource")
        {
            local, projects, into, output,
        };
        if (kind is not (SourceKinds.Linear or SourceKinds.Monday))
        {
            command.Options.Add(url);
        }

        if (kind == SourceKinds.Trello)
        {
            command.Options.Add(key);
        }

        return command.Handle(async (c, ct) =>
        {
            var token = await TokenAsync(ct) ?? throw new CliException($"Pipe the {name} token in, or set {TokenVariable}.");
            if (!c.Get(local))
            {
                if (c.Get(into) is not null || c.Get(output) is not null || c.Get(projects) is { Length: > 0 })
                {
                    throw new CliException("--project, --import and --out go with --local. Without it, Kupro reads the source itself: pick projects with `kupro import mapping`.");
                }

                var created = await (await c.InWorkspaceAsync(ct)).Imports.PostAsync(
                    new CreateImportRequest { SourceKind = kind, BaseUrl = c.Get(url), Token = token }, cancellationToken: ct);
                await c.Out.DoneAsync(created!, $"Created import {created!.Id}. Next: `kupro import mapping {created.Id}`.");
                return;
            }

            var transport = Cli.Runtime.Value?.SourceTransport;
            using var http = new HttpClient(transport ?? new SocketsHttpHandler(), disposeHandler: transport is null);
            var connector = Connector(kind, name, http, c.Get(url), c.Get(key), token);
            var wanted = c.Get(projects) ?? [];
            var file = c.Get(output) ?? new FileInfo(Path.Combine(Path.GetTempPath(), $"kupro-{kind}-{Guid.NewGuid():N}.zip"));
            try
            {
                await using (var zip = file.Create())
                {
                    await connector.WriteSnapshotAsync(zip, d => Scope(d, wanted), cursor: null, includeFiles: true, TimeProvider.System, ct);
                }
            }
            catch (SourceAccessException e)
            {
                file.Delete();
                throw new CliException($"{name} refused the read: {e.Message}");
            }
            catch
            {
                file.Delete();
                throw;
            }

            if (c.Get(output) is not null)
            {
                file.Refresh();
                await c.Out.MessageAsync($"Wrote {file.FullName} ({file.Length / 1024.0 / 1024.0:0.0} MB). Upload it with `kupro import snapshot {file.Name}`.");
                return;
            }

            try
            {
                var id = c.Get(into) ?? (await (await c.InWorkspaceAsync(ct)).Imports.PostAsync(new CreateImportRequest { SourceKind = SourceKinds.Snapshot }, cancellationToken: ct))!.Id!.Value;
                await Uploaded(c, id, await UploadAsync(c, id, file, ct));
            }
            finally
            {
                file.Delete();
            }
        });
    }

    private static ISourceConnector Connector(string kind, string name, HttpClient http, string? url, string? key, string token)
    {
        string Url() => url ?? throw new CliException($"Give the {name} address with --url.");
        return kind switch
        {
            SourceKinds.Jira => new JiraConnector(http, Url(), token),
            SourceKinds.AzureDevOps => new AzureDevOpsConnector(http, Url(), token),
            SourceKinds.GitHub => new GitHubConnector(http, url, token),
            SourceKinds.Linear => new LinearConnector(http, token),
            SourceKinds.Monday => new MondayConnector(http, token),
            SourceKinds.YouTrack => new YouTrackConnector(http, Url(), token),
            SourceKinds.Trello => new TrelloConnector(http, key ?? throw new CliException("Give your Trello API key with --key."), token),
            _ => throw new CliException($"Kupro can't read {name} from here."),
        };
    }

    /// <summary>The projects asked for, by key or ID; a name the source doesn't have is an error, not an empty read.</summary>
    private static ReadScope Scope(SnapshotDescription description, IReadOnlyList<string> wanted)
    {
        var ids = wanted.Select(w => description.Projects.FirstOrDefault(p => p.Id == w || string.Equals(p.Key, w, StringComparison.OrdinalIgnoreCase))?.Id
            ?? throw new CliException($"The source has no project {w}. It has: {string.Join(", ", description.Projects.Select(p => p.Key ?? p.Id))}.")).ToList();
        return new ReadScope { Projects = ids };
    }

    private static Command Create()
    {
        var source = new Argument<string>("source") { Description = "Source kind: azure-devops, linear, trello, jira..." };
        var url = new Option<string?>("--url") { Description = "The source's address (an Azure DevOps organisation, a Jira site)." };
        return new ApiCommand("create", $"Connect a source Kupro reads itself. The token comes from stdin or {TokenVariable}.", "createImport") { source, url }
            .Handle(async (c, ct) =>
            {
                var created = await (await c.InWorkspaceAsync(ct)).Imports.PostAsync(
                    new CreateImportRequest { SourceKind = c.Get(source), BaseUrl = c.Get(url), Token = await TokenAsync(ct) }, cancellationToken: ct);
                await c.Out.DoneAsync(created!, $"Created import {created!.Id}. Next: `kupro import mapping {created.Id}`.");
            });
    }

    private static Command List() => new ApiCommand("list", "The workspace's imports, newest first.", "listImports").Handle(async (c, ct) =>
    {
        var imports = await (await c.InWorkspaceAsync(ct)).Imports.GetAsync(cancellationToken: ct) ?? [];
        await c.Out.ListAsync(imports, ["ID", "SOURCE", "STATE", "AUTO-SYNC", "LAST RUN", "RESULT"], i =>
            [i.Id.ToString(), i.SourceKind, Cli.Dashed(i.State.ToString()!), i.AutoSync == true ? "on" : "off",
                i.LastRun?.CreatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                i.LastRun is { } r ? $"{Cli.Dashed(r.Kind.ToString()!)} {Cli.Dashed(r.State.ToString()!)}" : null]);
    });

    private static Command Show()
    {
        var id = Id();
        return new ApiCommand("show", "An import and its latest run.", "getImport") { id }.Handle(async (c, ct) =>
        {
            var i = await (await c.InWorkspaceAsync(ct)).Imports[c.Get(id)].GetAsync(cancellationToken: ct);
            await c.Out.ItemAsync(i!, ("Import", i!.Id.ToString()), ("Source", i.SourceKind), ("Address", i.BaseUrl), ("State", Cli.Dashed(i.State.ToString()!)),
                ("Token", i.HasToken == true ? i.TokenRejectedAt is null ? "kept" : "rejected: replace it with `kupro import token`" : null),
                ("Auto-sync", i.AutoSync == true ? "on" : "off"), ("Last run", i.LastRun is { } r ? $"{r.Id} {Cli.Dashed(r.State.ToString()!)}" : null));
        });
    }

    private static Command Upload()
    {
        var id = Id();
        var file = new Argument<FileInfo>("file") { Description = "A snapshot ZIP, CSV or Kupro export, as the import's source kind says." };
        return new ApiCommand("upload", "Replace the file an import reads; the next run reads this one.", "uploadImportSource") { id, file }.Handle(async (c, ct) =>
            await Uploaded(c, c.Get(id), await UploadAsync(c, c.Get(id), c.Get(file)!, ct)));
    }

    private static Command Source()
    {
        var id = Id();
        return new ApiCommand("source", "What the source holds, read live with the import's token: projects, people, types, statuses.", "describeImportSource") { id }
            .Handle(async (c, ct) =>
            {
                var d = await (await c.InWorkspaceAsync(ct)).Imports[c.Get(id)].Source.GetAsync(cancellationToken: ct);
                await c.Out.ItemAsync(d!, ("Projects", string.Join(", ", d!.Projects?.Select(p => p.Key ?? p.Name) ?? [])), ("People", d.People?.Count.ToString(CultureInfo.InvariantCulture)),
                    ("Types", string.Join(", ", d.ItemTypes?.Select(t => t.Name) ?? [])), ("Statuses", string.Join(", ", d.Statuses?.Select(s => s.Name) ?? [])));
            });
    }

    private static Command Mapping()
    {
        var id = Id();
        var set = new Option<string?>("--set") { Description = "Save this mapping file (JSON; - for stdin). Without it, prints the mapping in force, or the proposed one." };
        return new ApiCommand("mapping", "Print or save the mapping: people, projects, types, states, fields and effort.", "getImportMapping", "setImportMapping") { id, set }
            .Handle(async (c, ct) =>
            {
                var api = (await c.InWorkspaceAsync(ct)).Imports[c.Get(id)].Mapping;
                if (c.Get(set) is { } path)
                {
                    var json = path == "-" ? await (Cli.Runtime.Value?.In ?? Console.In).ReadToEndAsync(ct) : await File.ReadAllTextAsync(path, ct);
                    var mapping = await KiotaJsonSerializer.DeserializeAsync(json, ImportMapping.CreateFromDiscriminatorValue, ct);
                    var saved = await api.PutAsync(mapping!, cancellationToken: ct);
                    await c.Out.DoneAsync(saved!, $"Saved. Next: `kupro import plan {c.Get(id)}`.");
                    return;
                }

                // Always JSON: this is the file to edit and save back.
                var current = await api.GetAsync(cancellationToken: ct);
                await c.Out.Writer.WriteLineAsync(await KiotaJsonSerializer.SerializeAsStringAsync(current!.Mapping!, serializeOnlyChangedValues: false, ct), ct);
            });
    }

    private static Command Plan()
    {
        var id = Id();
        var noWait = new Option<bool>("--no-wait") { Description = "Start it and return; read it later with `import report`." };
        return new ApiCommand("plan", "Dry run: what a run would create, update and report, with examples. Writes nothing.", "planImport", "getImportRun") { id, noWait }
            .Handle(async (c, ct) =>
            {
                var api = (await c.InWorkspaceAsync(ct)).Imports[c.Get(id)];
                var run = await api.Plan.PostAsync(cancellationToken: ct);
                await ReportAsync(c, api, run!.Id!.Value, !c.Get(noWait), ct);
            });
    }

    private static Command Run()
    {
        var id = Id();
        var noWait = new Option<bool>("--no-wait") { Description = "Start it and return; follow it with `import report`." };
        return new ApiCommand("run", "Run the import (also \"sync now\"). One runs at a time per workspace.", "runImport", "getImportRun") { id, noWait }
            .Handle(async (c, ct) =>
            {
                var api = (await c.InWorkspaceAsync(ct)).Imports[c.Get(id)];
                var run = await api.Runs.PostAsync(cancellationToken: ct);
                await ReportAsync(c, api, run!.Id!.Value, !c.Get(noWait), ct);
            });
    }

    private static Command Runs()
    {
        var id = Id();
        return new ApiCommand("runs", "The import's runs, newest first.", "listImportRuns") { id }.Handle(async (c, ct) =>
        {
            var runs = await (await c.InWorkspaceAsync(ct)).Imports[c.Get(id)].Runs.GetAsync(cancellationToken: ct) ?? [];
            await c.Out.ListAsync(runs, ["ID", "KIND", "STATE", "STARTED", "ISSUES", "UPDATED", "CONFLICTS"], r =>
                [r.Id.ToString(), Cli.Dashed(r.Kind.ToString()!), Cli.Dashed(r.State.ToString()!), r.StartedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    r.Counts?.Issues?.ToString(CultureInfo.InvariantCulture), r.Counts?.Updated?.ToString(CultureInfo.InvariantCulture), r.Counts?.Conflicts?.ToString(CultureInfo.InvariantCulture)]);
        });
    }

    private static Command Report()
    {
        var id = Id();
        var run = new Argument<Guid?>("run") { Description = "Run id; the latest run if left out.", Arity = ArgumentArity.ZeroOrOne };
        return new ApiCommand("report", "A run's plan or report: counts with examples, conflicts, deletions in the source.", "getImportRun") { id, run }
            .Handle(async (c, ct) =>
            {
                var api = (await c.InWorkspaceAsync(ct)).Imports[c.Get(id)];
                var runId = c.Get(run) ?? (await api.GetAsync(cancellationToken: ct))?.LastRun?.Id ?? throw new CliException("The import hasn't run yet.");
                await ReportAsync(c, api, runId, wait: false, ct);
            });
    }

    private static Command Sync()
    {
        var id = Id();
        var auto = new Option<string>("--auto") { Description = "on or off: run hourly until Finish.", Required = true };
        auto.AcceptOnlyFromAmong("on", "off");
        return new ApiCommand("sync", "Turn hourly auto-sync on or off. To sync now, use `import run`.", "setImportAutoSync") { id, auto }.Handle(async (c, ct) =>
        {
            var i = await (await c.InWorkspaceAsync(ct)).Imports[c.Get(id)].AutoSync.PutAsync(new AutoSyncRequest { Enabled = c.Get(auto) == "on" }, cancellationToken: ct);
            await c.Out.DoneAsync(i!, $"Auto-sync is {(i!.AutoSync == true ? "on" : "off")}.");
        });
    }

    private static Command Token()
    {
        var id = Id();
        return new ApiCommand("token", $"Replace the source token (from stdin or {TokenVariable}); the next run continues where the last stopped.", "replaceImportToken") { id }
            .Handle(async (c, ct) =>
            {
                var token = await TokenAsync(ct) ?? throw new CliException($"Pipe the token in, or set {TokenVariable}.");
                var i = await (await c.InWorkspaceAsync(ct)).Imports[c.Get(id)].Token.PutAsync(new ImportTokenRequest { Token = token }, cancellationToken: ct);
                await c.Out.DoneAsync(i!, "Token replaced.");
            });
    }

    private static Command Conflicts()
    {
        var id = Id();
        var all = new Option<bool>("--all") { Description = "Resolved ones too." };
        var keep = new Option<Guid?>("--keep") { Description = "Keep Kupro's value for this conflict." };
        var useSource = new Option<Guid?>("--use-source") { Description = "Use the source's value for this conflict." };
        return new ApiCommand("conflicts", "Fields changed on both sides since the last run; resolve one with --keep or --use-source.",
            "listImportConflicts", "resolveImportConflict") { id, all, keep, useSource }.Handle(async (c, ct) =>
        {
            var api = (await c.InWorkspaceAsync(ct)).Imports[c.Get(id)].Conflicts;
            if ((c.Get(keep), c.Get(useSource)) is var (k, u) && (k ?? u) is { } conflict)
            {
                if (k is not null && u is not null)
                {
                    throw new CliException("Pick one: --keep or --use-source.");
                }

                var resolved = await api[conflict].Resolve.PostAsync(new ResolveConflictRequest { UseSource = u is not null }, cancellationToken: ct);
                await c.Out.DoneAsync(resolved!, u is not null ? "Used the source's value." : "Kept Kupro's value.");
                return;
            }

            var list = await api.GetAsync(r => r.QueryParameters.All = c.Get(all), ct) ?? [];
            await c.Out.ListAsync(list, ["ID", "ISSUE", "FIELD", "KUPRO", "SOURCE"], x => [x.Id.ToString(), x.IssueKey, x.Field, x.Kupro, x.Source]);
        });
    }

    private static Command Undo()
    {
        var id = Id();
        var check = new Option<bool>("--check") { Description = "Only show what Undo would lose and what blocks it." };
        return new ApiCommand("undo", "Delete everything the import created. Refused while records it didn't create point at its issues.", "checkImportUndo", "undoImport") { id, check }
            .Handle(async (c, ct) =>
            {
                var api = (await c.InWorkspaceAsync(ct)).Imports[c.Get(id)].Undo;
                if (c.Get(check))
                {
                    var found = await api.GetAsync(cancellationToken: ct);
                    await Checked(c, found!);
                    return;
                }

                var result = await api.PostAsync(cancellationToken: ct);
                if (result!.Undone != true)
                {
                    await Checked(c, result.Check!);
                    throw new CliException("Undo is blocked: move or delete the records above first.");
                }

                await c.Out.DoneAsync(result, $"Undone: {result.Issues} issues and what came with them are deleted.");
            });
    }

    private static Command Finish()
    {
        var id = Id();
        return new ApiCommand("finish", "Finish the migration: auto-sync stops and the token is deleted. Aliases and history stay.", "finishImport") { id }
            .Handle(async (c, ct) =>
            {
                var i = await (await c.InWorkspaceAsync(ct)).Imports[c.Get(id)].Finish.PostAsync(cancellationToken: ct);
                await c.Out.DoneAsync(i!, "Finished. The import can no longer run or be undone.");
            });
    }

    // ---------------------------------------------------------------- shared

    /// <summary>The body is the file itself; the generated client has no way to send one, so this builds the request.</summary>
    private static async Task<ImportUpload> UploadAsync(CommandContext c, Guid id, FileInfo file, CancellationToken ct)
    {
        if (!file.Exists)
        {
            throw new CliException($"There is no file {file.FullName}.");
        }

        await using var content = file.OpenRead();
        var request = (await c.InWorkspaceAsync(ct)).Imports[id].Source.ToPutRequestInformation();
        request.SetStreamContent(content, "application/octet-stream");
        var errors = new Dictionary<string, ParsableFactory<IParsable>> { ["XXX"] = ProblemDetails.CreateFromDiscriminatorValue };
        return (await c.Adapter.SendAsync(request, ImportUpload.CreateFromDiscriminatorValue, errors, ct))!;
    }

    private static async Task Uploaded(CommandContext c, Guid id, ImportUpload upload)
    {
        if (upload.Csv is { } csv)
        {
            await c.Out.DoneAsync(upload, $"""
                Import {id}: {csv.Rows} rows, columns {string.Join(", ", csv.Headers ?? [])} ({csv.Encoding}, '{csv.Delimiter}').
                {(csv.Columns?.Preset is { } preset ? $"Recognised a {preset} export." : "Mapped the columns by name.")} Check them with `kupro import mapping {id}`, then `kupro import plan {id}`.
                """);
            return;
        }

        await c.Out.DoneAsync(upload, $"Import {id}: {upload.Description?.Projects?.Count ?? 0} projects, {upload.Description?.People?.Count ?? 0} people. Next: `kupro import mapping {id}`.");
    }

    private static async Task ReportAsync(CommandContext c, Api.Api.V1.Workspaces.Item.Imports.Item.ImportsItemRequestBuilder api, Guid runId, bool wait, CancellationToken ct)
    {
        var detail = await api.Runs[runId].GetAsync(cancellationToken: ct);
        while (wait && detail!.Run!.State is ImportRunState.Queued or ImportRunState.Running)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            detail = await api.Runs[runId].GetAsync(cancellationToken: ct);
        }

        var run = detail!.Run!;
        if (run.State == ImportRunState.Failed)
        {
            throw new CliException($"The run failed: {run.Error}");
        }

        static string? Count(PlanCount? count) => count?.Count is > 0 ? $"{count.Count} (e.g. {string.Join(", ", count.Samples?.Select(s => s.Key ?? s.Title) ?? [])})" : null;
        if (detail.Plan is { } plan)
        {
            await c.Out.ItemAsync(detail, ("Run", run.Id.ToString()), ("State", Cli.Dashed(run.State.ToString()!)), ("Create", Count(plan.Create) ?? "0"),
                ("Update", Count(plan.Update)), ("Conflicts", Count(plan.Conflicts)), ("Keys taken", Count(plan.KeysTaken)), ("Re-parented", Count(plan.Reparented)),
                ("Skipped", Count(plan.SkippedByScope)), ("Deleted there", Count(plan.DeletedInSource)), ("Statuses to check", Count(plan.StatusesToReview)),
                ("Project keys taken", plan.ProjectKeysTaken is { Count: > 0 } keys ? string.Join(", ", keys) : null),
                ("Comments", plan.Comments?.ToString(CultureInfo.InvariantCulture)), ("Attachments", plan.Attachments?.ToString(CultureInfo.InvariantCulture)));
            return;
        }

        var counts = run.Counts;
        await c.Out.ItemAsync(detail, ("Run", run.Id.ToString()), ("State", Cli.Dashed(run.State.ToString()!)),
            ("Created", counts?.Issues?.ToString(CultureInfo.InvariantCulture)), ("Updated", counts?.Updated?.ToString(CultureInfo.InvariantCulture)),
            ("Comments", counts?.Comments?.ToString(CultureInfo.InvariantCulture)), ("Attachments", counts?.Attachments?.ToString(CultureInfo.InvariantCulture)),
            ("Conflicts", Count(detail.Report?.Conflicts)), ("Deleted there", Count(detail.Report?.DeletedInSource)));
    }

    private static async Task Checked(CommandContext c, ImportUndoCheck check) =>
        await c.Out.ItemAsync(check, ("Edited since", check.EditedCount is > 0 ? $"{check.EditedCount} ({string.Join(", ", check.Edited ?? [])})" : "none"),
            ("Blocked by", check.BlockerCount is > 0 ? string.Join("; ", check.Blockers?.Select(b => $"{b.Kind} on {b.IssueKey} ({b.Id})") ?? []) : "nothing"));

    /// <summary>From stdin when something is piped in, else KUPRO_IMPORT_TOKEN; never from an argument.</summary>
    private static async Task<string?> TokenAsync(CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable(TokenVariable) is { Length: > 0 } fromEnv)
        {
            return fromEnv.Trim();
        }

        var input = Cli.Runtime.Value?.In ?? (Console.IsInputRedirected ? Console.In : null);
        return input is null ? null : (await input.ReadToEndAsync(ct)).Trim() is { Length: > 0 } piped ? piped : null;
    }
}
