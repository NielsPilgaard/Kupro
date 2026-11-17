using System.Text.Json;
using Kupro.Api.Features.Issues;
using Kupro.Import.Csv;
using Kupro.Import.Kupro;
using Kupro.Import.Snapshot;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Api.Features.Imports;

/// <summary>What Kupro read from an uploaded CSV, for the column-mapping step.</summary>
/// <param name="Delimiter">The character that separates fields.</param>
/// <param name="Encoding"><c>utf-8</c> or <c>windows-1252</c>.</param>
/// <param name="Samples">The first rows, as written.</param>
/// <param name="Columns">Which column holds what: a known export's, or a guess by name.</param>
public sealed record CsvPreview(string Delimiter, string Encoding, IReadOnlyList<string> Headers, int Rows, IReadOnlyList<IReadOnlyList<string>> Samples, CsvColumns Columns);

/// <summary>A CSV as an import source (task 023): the file is kept, and turned into a snapshot when a run starts.</summary>
public sealed partial class ImportService
{
    /// <summary>The project a CSV without a project column becomes, until the mapping names it.</summary>
    public const string CsvProjectName = "CSV import";

    private const int CsvSamples = 5;

    public async Task<IssueResult<CsvPreview>> StoreCsvAsync(Guid importId, string path, CancellationToken ct)
    {
        if (!snapshots.Enabled)
        {
            return Unavailable;
        }

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var import = await db.WorkspaceImports.AsNoTracking().SingleOrDefaultAsync(i => i.Id == importId, ct);
            if (Unusable(import, importId) is { } error)
            {
                return error;
            }

            if (import!.SourceKind != SourceKinds.Csv)
            {
                return new IssueError("not_csv", "This import doesn't read a CSV file.");
            }
        }

        CsvTable table;
        try
        {
            await using var file = File.OpenRead(path);
            table = await CsvTable.ReadAsync(file, ct);
        }
        catch (FormatException ex)
        {
            return new IssueError("invalid_csv", ex.Message);
        }

        await snapshots.StoreCsvAsync(workspace.RequireWorkspaceId(), importId, path, table, ct);
        return Preview(table, CsvColumns.Propose(table.Headers));
    }

    /// <summary>The stored CSV's preview again, with the columns in force: the wizard shows it after a reload.</summary>
    public async Task<IssueResult<CsvPreview>> GetCsvPreviewAsync(Guid importId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var import = await db.WorkspaceImports.AsNoTracking().SingleOrDefaultAsync(i => i.Id == importId, ct);
        if (Unusable(import, importId) is { } error)
        {
            return error;
        }

        if (import!.SourceKind != SourceKinds.Csv)
        {
            return new IssueError("not_csv", "This import doesn't read a CSV file.");
        }

        if (await snapshots.ReadCsvAsync(import.WorkspaceId, importId, ct) is not { } table)
        {
            return new IssueError("no_snapshot", "Upload the CSV file first.");
        }

        return Preview(table, CsvColumns.Fit(SavedColumns(import), table.Headers));
    }

    private static CsvPreview Preview(CsvTable table, CsvColumns columns) => new(
        table.Delimiter == '\t' ? "tab" : table.Delimiter.ToString(), table.Encoding, table.Headers, table.Rows.Count,
        [.. table.Rows.Take(CsvSamples).Select(r => (IReadOnlyList<string>)r)], columns);

    private static CsvColumns? SavedColumns(WorkspaceImport import) => import.Mapping is { } json ? ImportMapping.FromJson(json).Csv : null;

    /// <summary>Keeps a Kupro export ZIP as the import's source, after checking Kupro can read it.</summary>
    public async Task<IssueResult<SourceDescription>> StoreExportAsync(Guid importId, string path, CancellationToken ct)
    {
        if (!snapshots.Enabled)
        {
            return Unavailable;
        }

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var import = await db.WorkspaceImports.AsNoTracking().SingleOrDefaultAsync(i => i.Id == importId, ct);
            if (Unusable(import, importId) is { } error)
            {
                return error;
            }

            if (import!.SourceKind != SourceKinds.Kupro)
            {
                return new IssueError("not_kupro_export", "This import doesn't read a Kupro export.");
            }
        }

        SourceDescription description;
        try
        {
            await using var file = File.OpenRead(path);
            using var export = await KuproExportConnector.OpenAsync(file, options.Value.MaxExportJsonMegabytes, ct);
            description = await export.DescribeAsync(ct);
        }
        catch (Exception ex) when (ex is SnapshotFormatException or InvalidDataException or JsonException)
        {
            return new IssueError("invalid_export", ex is SnapshotFormatException ? ex.Message : "This is not a Kupro export ZIP.");
        }

        await snapshots.StoreSourceAsync(workspace.RequireWorkspaceId(), importId, path, "application/zip", ct);
        return description;
    }

    /// <summary>
    /// The mapping Kupro proposes from what the source holds: people matched to members by email and name, types and
    /// states by name and category, a CSV's columns by header. The People step of the CLI starts from this file.
    /// </summary>
    public async Task<IssueResult<ImportMapping>> ProposeMappingAsync(Guid importId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var import = await db.WorkspaceImports.AsNoTracking().SingleOrDefaultAsync(i => i.Id == importId, ct);
        if (Unusable(import, importId) is { } error)
        {
            return error;
        }

        var members = await db.Members.AsNoTracking()
            .Where(m => m.Kind == Workspaces.MemberKind.Human && m.Status == Workspaces.MemberStatus.Active)
            .Select(m => new MemberCandidate(
                m.Id,
                m.DisplayName ?? db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefault()!,
                db.Users.Where(u => u.Id == m.UserId).Select(u => u.Email).FirstOrDefault()))
            .ToListAsync(ct);

        if (import!.SourceKind == SourceKinds.Csv)
        {
            var csv = await ReadCsvAsync(import, ct);
            return csv.Error is { } csvError ? csvError : ImportMappingDefaults.Propose(csv.Value!.Description, members) with { Csv = csv.Value.Columns };
        }

        if (import.SourceKind == SourceKinds.Kupro)
        {
            await using var local = await snapshots.OpenLocalSourceAsync(import.WorkspaceId, importId, ct);
            if (local is null)
            {
                return new IssueError("no_snapshot", "Upload the export first.");
            }

            using var export = await KuproExportConnector.OpenAsync(local, options.Value.MaxExportJsonMegabytes, ct);
            return ImportMappingDefaults.Propose(await export.DescribeAsync(ct), members) with { MilestoneVersions = export.Milestones() };
        }

        var described = import.EncryptedToken is not null ? await DescribeAsync(importId, ct) : await DescribeUploadAsync(import, ct);
        return described.Error is { } describeError ? describeError : ImportMappingDefaults.Propose(described.Value!, members);
    }

    /// <summary>What an uploaded source holds, read from the stored file: a CSV with its saved or proposed columns, an export, or a snapshot.</summary>
    private async Task<IssueResult<SourceDescription>> DescribeUploadAsync(WorkspaceImport import, CancellationToken ct)
    {
        if (import.SourceKind == SourceKinds.Csv)
        {
            var csv = await ReadCsvAsync(import, ct);
            return csv.Error is { } error ? error : csv.Value!.Description;
        }

        if (import.SourceKind == SourceKinds.Kupro)
        {
            await using var local = await snapshots.OpenLocalSourceAsync(import.WorkspaceId, import.Id, ct);
            if (local is null)
            {
                return new IssueError("no_snapshot", "Upload the export first.");
            }

            using var export = await KuproExportConnector.OpenAsync(local, options.Value.MaxExportJsonMegabytes, ct);
            return await export.DescribeAsync(ct);
        }

        await using var file = await snapshots.OpenAsync(import.WorkspaceId, import.Id, ct);
        if (file is null)
        {
            return new IssueError("no_snapshot", "The import has no snapshot yet.");
        }

        await using var snapshot = await SnapshotReader.OpenAsync(file, ct);
        return Described(snapshot.Header);
    }

    private static SourceDescription Described(SnapshotHeader header) => new()
    {
        Source = header.Source,
        Projects = header.Projects,
        People = header.People,
        ItemTypes = header.ItemTypes,
        Statuses = header.Statuses,
        Fields = header.Fields,
    };

    private sealed record CsvRead(SourceDescription Description, CsvColumns Columns);

    private async Task<IssueResult<CsvRead>> ReadCsvAsync(WorkspaceImport import, CancellationToken ct)
    {
        if (await snapshots.ReadCsvAsync(import.WorkspaceId, import.Id, ct) is not { } table)
        {
            return new IssueError("no_snapshot", "Upload the CSV file first.");
        }

        var columns = CsvColumns.Fit(SavedColumns(import), table.Headers);
        return new CsvRead(await new CsvConnector(table, columns, CsvProjectName, clock).DescribeAsync(ct), columns);
    }
}
