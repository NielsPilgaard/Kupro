using System.Text.Json;
using System.Text.RegularExpressions;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Workspaces;
using Kupro.Import.Snapshot;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Api.Features.Imports;

/// <summary>An import as the owner sees it. The token itself is never returned; <see cref="HasToken"/> says whether one is kept.</summary>
public sealed record ImportResponse(
    Guid Id,
    string SourceKind,
    string? BaseUrl,
    ImportState State,
    bool AutoSync,
    bool HasToken,
    DateTimeOffset? TokenRejectedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? LastChangeAt,
    DateTimeOffset? FinishedAt,
    ImportRunResponse? LastRun);

/// <summary>A dry run or a commit. <see cref="Counts"/> is what a commit did so far; a finished one's report is on <c>getImportRun</c>.</summary>
public sealed record ImportRunResponse(
    Guid Id, ImportRunKind Kind, ImportRunState State, ImportRunCounts? Counts, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);

/// <summary>A run with what it found: the plan of a dry run, or a commit's report.</summary>
public sealed record ImportRunDetail(ImportRunResponse Run, ImportPlan? Plan, ImportRunReport? Report);

/// <summary>The mapping in force, or the one Kupro proposes when none is saved yet.</summary>
public sealed record ImportMappingResponse(ImportMapping Mapping, bool Saved);

/// <summary>
/// A field both sides changed. <see cref="Kupro"/> and <see cref="Source"/> are for reading (names, keys, dates); the
/// raw values are Kupro's own (member and sprint ids, enum names).
/// </summary>
public sealed record ImportConflictResponse(
    Guid Id, string IssueKey, string IssueTitle, string Field, string? Kupro, string? Source, JsonElement? KuproValue, JsonElement? SourceValue,
    ImportConflictState State, DateTimeOffset UpdatedAt);

/// <param name="UseSource">True applies the source's value; false keeps Kupro's.</param>
public sealed record ResolveConflictRequest(bool UseSource);

/// <param name="Enabled">Run hourly until Finish; off after 30 days with no change on either side.</param>
public sealed record AutoSyncRequest(bool Enabled);

/// <param name="Token">The new source token. Stored encrypted and never returned.</param>
public sealed record ImportTokenRequest([property: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.MaxLength(4096)] string Token);

/// <summary>What an uploaded file holds: a snapshot's or an export's description, or a CSV's columns.</summary>
public sealed record ImportUpload(SourceDescription? Description, CsvPreview? Csv);

/// <summary>Reads for the import surfaces (REST, CLI and the read-only MCP tool).</summary>
public sealed partial class ImportService
{
    private const int MaxRuns = 50;
    private const int MaxConflicts = 200;
    private const int MaxShown = 200;

    public async Task<IReadOnlyList<ImportResponse>> ListAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var imports = await db.WorkspaceImports.AsNoTracking().OrderByDescending(i => i.CreatedAt).ToListAsync(ct);
        var ids = imports.Select(i => i.Id).ToList();
        var last = (await db.ImportRuns.AsNoTracking().Where(r => ids.Contains(r.ImportId))
                .GroupBy(r => r.ImportId).Select(g => g.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).First()).ToListAsync(ct))
            .ToDictionary(r => r.ImportId);
        return [.. imports.Select(i => Response(i, last.GetValueOrDefault(i.Id)))];
    }

    public async Task<IssueResult<ImportResponse>> GetAsync(Guid importId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var import = await db.WorkspaceImports.AsNoTracking().SingleOrDefaultAsync(i => i.Id == importId, ct);
        if (import is null)
        {
            return NotFound(importId);
        }

        var last = await db.ImportRuns.AsNoTracking().Where(r => r.ImportId == importId).OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).FirstOrDefaultAsync(ct);
        return Response(import, last);
    }

    public async Task<IssueResult<IReadOnlyList<ImportRunResponse>>> ListRunsAsync(Guid importId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.WorkspaceImports.AnyAsync(i => i.Id == importId, ct))
        {
            return NotFound(importId);
        }

        var runs = await db.ImportRuns.AsNoTracking().Where(r => r.ImportId == importId).OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Take(MaxRuns).ToListAsync(ct);
        return runs.Select(RunResponse).ToList();
    }

    /// <summary>A run by its id; with <paramref name="importId"/>, only if it belongs to that import.</summary>
    public async Task<IssueResult<ImportRunDetail>> GetRunAsync(Guid? importId, Guid runId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await db.ImportRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId && (importId == null || r.ImportId == importId), ct);
        if (run is null)
        {
            return new IssueError("run_not_found", $"No import run {runId}.");
        }

        var web = JsonSerializerOptions.Web;
        return new ImportRunDetail(
            RunResponse(run),
            run.Kind == ImportRunKind.DryRun && run.Report is { } plan ? JsonSerializer.Deserialize<ImportPlan>(plan, web) : null,
            run.Kind == ImportRunKind.Commit && run.Report is { } report ? JsonSerializer.Deserialize<ImportRunReport>(report, web) : null);
    }

    public async Task<IssueResult<ImportMappingResponse>> GetMappingAsync(Guid importId, CancellationToken ct)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var saved = await db.WorkspaceImports.AsNoTracking().Where(i => i.Id == importId).Select(i => new { i.Mapping }).SingleOrDefaultAsync(ct);
            if (saved is null)
            {
                return NotFound(importId);
            }

            if (saved.Mapping is { } json)
            {
                return new ImportMappingResponse(ImportMapping.FromJson(json), Saved: true);
            }
        }

        var proposed = await ProposeMappingAsync(importId, ct);
        return proposed.Error is { } error ? error : new ImportMappingResponse(proposed.Value!, Saved: false);
    }

    /// <summary>Saves the owner's choices. Applies from the next run; what earlier runs imported keeps its shape.</summary>
    public async Task<IssueResult<ImportMapping>> SetMappingAsync(Guid importId, ImportMapping mapping, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var import = await db.WorkspaceImports.SingleOrDefaultAsync(i => i.Id == importId, ct);
        if (Unusable(import, importId) is { } error)
        {
            return error;
        }

        if (mapping.Projects.Values.FirstOrDefault(p => !ProjectKeyPattern().IsMatch(p.Key)) is { } badKey)
        {
            return new IssueError("invalid_mapping", $"{badKey.Key} isn't a valid project key: 2 to 10 letters or digits, starting with a letter.");
        }

        if (mapping.HoursPerPoint is < 0 or > 1000)
        {
            return new IssueError("invalid_mapping", "Hours per point is between 0 and 1000.");
        }

        var memberIds = mapping.People.Values.Where(p => p.Action == PersonAction.Member).Select(p => p.MemberId).ToList();
        if (memberIds.Any(id => id is null))
        {
            return new IssueError("invalid_mapping", "A person mapped to a member needs the member's id.");
        }

        var ids = memberIds.OfType<Guid>().Distinct().ToList();
        var found = await db.Members.CountAsync(m => ids.Contains(m.Id) && m.Kind == MemberKind.Human
            && (m.Status == MemberStatus.Active || m.Status == MemberStatus.Placeholder), ct);
        if (found != ids.Count)
        {
            return new IssueError("invalid_mapping", "A person is mapped to someone who isn't an active member or placeholder of this workspace.");
        }

        import!.Mapping = mapping.ToJson();
        await db.SaveChangesAsync(ct);
        return mapping;
    }

    /// <summary>Open conflicts by default, oldest first; <paramref name="after"/> continues from the last one seen.</summary>
    public async Task<IssueResult<IReadOnlyList<ImportConflictResponse>>> ListConflictsAsync(Guid importId, bool all, Guid? after, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.WorkspaceImports.AnyAsync(i => i.Id == importId, ct))
        {
            return NotFound(importId);
        }

        var rows = await db.ImportConflicts.AsNoTracking()
            .Where(c => c.ImportId == importId && (all || c.State == ImportConflictState.Open) && (after == null || c.Id.CompareTo(after.Value) > 0))
            .OrderBy(c => c.Id).Take(MaxConflicts)
            .Join(db.Issues, c => c.IssueId, i => i.Id, (c, i) => new { Conflict = c, i.Key, i.Title })
            .ToListAsync(ct);

        var values = rows.SelectMany(r => new[] { (r.Conflict.Field, r.Conflict.KuproValue), (r.Conflict.Field, r.Conflict.SourceValue) })
            .Select(v => (v.Field, Value: Parse(v.Item2))).ToList();
        Guid[] IdsOf(string field) => [.. values.Where(v => v.Field == field && v.Value?.ValueKind == JsonValueKind.String)
            .Select(v => Guid.TryParse(v.Value!.Value.GetString(), out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct()];
        var (memberIds, parentIds, sprintIds) = (IdsOf(ImportedField.Assignee), IdsOf(ImportedField.Parent), IdsOf(ImportedField.Sprint));
        var members = await db.Members.AsNoTracking().Where(m => memberIds.Contains(m.Id))
            .Select(m => new { m.Id, Name = m.DisplayName ?? db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefault() ?? m.AgentName })
            .ToDictionaryAsync(m => m.Id, m => m.Name, ct);
        var parents = await db.Issues.AsNoTracking().Where(i => parentIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Key, ct);
        var sprints = await db.Sprints.AsNoTracking().Where(s => sprintIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        string? Show(string field, JsonElement? value) => value switch
        {
            null or { ValueKind: JsonValueKind.Null } => null,
            { ValueKind: JsonValueKind.Array } list => string.Join(", ", list.EnumerateArray().Select(v => v.ToString())),
            { ValueKind: JsonValueKind.String } text when Guid.TryParse(text.GetString(), out var id) => field switch
            {
                ImportedField.Assignee => members.GetValueOrDefault(id),
                ImportedField.Parent => parents.GetValueOrDefault(id),
                ImportedField.Sprint => sprints.GetValueOrDefault(id),
                _ => text.GetString(),
            },
            { ValueKind: JsonValueKind.String } text when field == ImportedField.Description => Short(text.GetString()!),
            { } other => other.ToString(),
        };

        return rows.Select(r =>
        {
            var (kupro, source) = (Parse(r.Conflict.KuproValue), Parse(r.Conflict.SourceValue));
            return new ImportConflictResponse(r.Conflict.Id, r.Key, r.Title, r.Conflict.Field, Show(r.Conflict.Field, kupro), Show(r.Conflict.Field, source),
                kupro, source, r.Conflict.State, r.Conflict.UpdatedAt);
        }).ToList();
    }

    /// <summary>Takes the file the import reads: a snapshot, a CSV or a Kupro export, as its source kind says.</summary>
    public async Task<IssueResult<ImportUpload>> UploadAsync(Guid importId, string path, CancellationToken ct)
    {
        string? kind;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            kind = await db.WorkspaceImports.Where(i => i.Id == importId).Select(i => i.SourceKind).SingleOrDefaultAsync(ct);
        }

        switch (kind)
        {
            case null:
                return NotFound(importId);
            case SourceKinds.Csv:
                var csv = await StoreCsvAsync(importId, path, ct);
                return csv.Error is { } csvError ? csvError : new ImportUpload(null, csv.Value);
            case SourceKinds.Kupro:
                var export = await StoreExportAsync(importId, path, ct);
                return export.Error is { } exportError ? exportError : new ImportUpload(export.Value, null);
            default:
                var snapshot = await StoreSnapshotAsync(importId, path, ct);
                return snapshot.Error is { } snapshotError ? snapshotError : new ImportUpload(new SourceDescription
                {
                    Source = snapshot.Value!.Source,
                    Projects = snapshot.Value.Projects,
                    People = snapshot.Value.People,
                    ItemTypes = snapshot.Value.ItemTypes,
                    Statuses = snapshot.Value.Statuses,
                    Fields = snapshot.Value.Fields,
                }, null);
        }
    }

    private static ImportResponse Response(WorkspaceImport i, ImportRun? last) =>
        new(i.Id, i.SourceKind, i.BaseUrl, i.State, i.AutoSync, i.EncryptedToken is not null, i.TokenRejectedAt, i.CreatedAt, i.LastRunAt, i.LastChangeAt, i.FinishedAt,
            last is null ? null : RunResponse(last));

    private static ImportRunResponse RunResponse(ImportRun r) =>
        new(r.Id, r.Kind, r.State, r.Counts is { } json ? JsonSerializer.Deserialize<ImportRunCounts>(json, JsonSerializerOptions.Web) : null, r.Error,
            r.CreatedAt, r.StartedAt, r.CompletedAt);

    private static IssueError NotFound(Guid importId) => new("import_not_found", $"No import {importId}.");

    private static JsonElement? Parse(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();

    private static string Short(string text) => text.Length <= MaxShown ? text : string.Concat(text.AsSpan(0, MaxShown), "…");

    [GeneratedRegex(ProjectService.KeyPattern)]
    private static partial Regex ProjectKeyPattern();
}
