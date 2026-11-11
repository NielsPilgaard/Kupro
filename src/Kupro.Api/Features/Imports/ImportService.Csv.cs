using Kupro.Api.Features.Issues;
using Kupro.Import.Csv;
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

            if (import!.SourceKind != Kupro.Import.Snapshot.SourceKinds.Csv)
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

        await snapshots.StoreSourceAsync(workspace.RequireWorkspaceId(), importId, path, "text/csv", ct);
        return new CsvPreview(
            table.Delimiter == '\t' ? "tab" : table.Delimiter.ToString(), table.Encoding, table.Headers, table.Rows.Count,
            [.. table.Rows.Take(CsvSamples).Select(r => (IReadOnlyList<string>)r)], CsvColumns.Propose(table.Headers));
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

        if (import!.SourceKind == Kupro.Import.Snapshot.SourceKinds.Csv)
        {
            await using var source = await snapshots.OpenSourceAsync(import.WorkspaceId, importId, ct);
            if (source is null)
            {
                return new IssueError("no_snapshot", "Upload the CSV file first.");
            }

            var table = await CsvTable.ReadAsync(source, ct);
            var saved = import.Mapping is { } json ? ImportMapping.FromJson(json).Csv : null;
            var columns = saved ?? CsvColumns.Propose(table.Headers);
            var description = await new CsvConnector(table, columns, CsvProjectName, clock).DescribeAsync(ct);
            return ImportMappingDefaults.Propose(description, members) with { Csv = columns };
        }

        if (import.EncryptedToken is not null)
        {
            var described = await DescribeAsync(importId, ct);
            return described.Error is { } describeError ? describeError : ImportMappingDefaults.Propose(described.Value!, members);
        }

        await using var file = await snapshots.OpenAsync(import.WorkspaceId, importId, ct);
        if (file is null)
        {
            return new IssueError("no_snapshot", "The import has no snapshot yet.");
        }

        await using var snapshot = await Kupro.Import.Snapshot.SnapshotReader.OpenAsync(file, ct);
        return ImportMappingDefaults.Propose(snapshot.Header, members);
    }
}
