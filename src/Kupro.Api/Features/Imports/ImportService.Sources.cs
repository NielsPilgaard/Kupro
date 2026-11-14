using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Kupro.Api.Features.Issues;
using Kupro.Import;
using Kupro.Import.Snapshot;
using Microsoft.EntityFrameworkCore;
using Polly.Timeout;

namespace Kupro.Api.Features.Imports;

/// <param name="SourceKind">Where the data comes from: <c>jira</c>, <c>azure-devops</c>, <c>linear</c>, <c>trello</c>, <c>csv</c>, <c>snapshot</c>...</param>
/// <param name="BaseUrl">The source's address, for sources that have one (an Azure DevOps organisation, a Jira site).</param>
/// <param name="Token">The source token, for a server-side import. Stored encrypted and never returned.</param>
public sealed record CreateImportRequest(
    [Required, RegularExpression("^[a-z][a-z0-9-]{0,31}$")] string SourceKind,
    [Url, MaxLength(2048)] string? BaseUrl = null,
    [MaxLength(4096)] string? Token = null);

/// <summary>Creating an import and what it holds of the source: its token, its auto-sync, its description (ADR-019 Source access).</summary>
public sealed partial class ImportService
{
    public async Task<IssueResult<WorkspaceImport>> CreateAsync(CreateImportRequest request, CancellationToken ct)
    {
        if (request.Token is not null && TokenError(request.SourceKind) is { } error)
        {
            return error;
        }

        if (workspace.MemberId == Guid.Empty)
        {
            throw new InvalidOperationException("An import is created by a member: its owner gets the emails.");
        }

        var import = new WorkspaceImport
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.RequireWorkspaceId(),
            SourceKind = request.SourceKind,
            BaseUrl = request.BaseUrl?.Trim().TrimEnd('/'),
            State = ImportState.Active,
            CreatedById = workspace.MemberId,
            CreatedAt = clock.GetUtcNow(),
        };
        import.EncryptedToken = request.Token is { } token ? tokens.Protect(import.Id, token.Trim()) : null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.WorkspaceImports.Add(import);
        await db.SaveChangesAsync(ct);
        return import;
    }

    /// <summary>A new token after the source refused the old one: auto-sync resumes, and the next run reads on from the saved cursor.</summary>
    public async Task<IssueResult<WorkspaceImport>> ReplaceTokenAsync(Guid importId, string token, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var import = await db.WorkspaceImports.SingleOrDefaultAsync(i => i.Id == importId, ct);
        if (Unusable(import, importId) is { } error)
        {
            return error;
        }

        if (TokenError(import!.SourceKind) is { } tokenError)
        {
            return tokenError;
        }

        import.EncryptedToken = tokens.Protect(import.Id, token.Trim());
        import.TokenRejectedAt = null;
        await db.SaveChangesAsync(ct);
        return import;
    }

    /// <summary>Hourly runs until Finish (ADR-019 Mode). Only an import Kupro reads itself can sync: an upload has nothing to fetch.</summary>
    public async Task<IssueResult<WorkspaceImport>> SetAutoSyncAsync(Guid importId, bool on, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var import = await db.WorkspaceImports.SingleOrDefaultAsync(i => i.Id == importId, ct);
        if (Unusable(import, importId) is { } error)
        {
            return error;
        }

        if (on && import!.EncryptedToken is null)
        {
            return new IssueError("auto_sync_needs_token", "Auto-sync reads the source, so the import needs a token. Uploaded snapshots are synced by uploading again.");
        }

        import!.AutoSync = on;
        await db.SaveChangesAsync(ct);
        return import;
    }

    /// <summary>Projects, people, types, statuses and fields, read live for the wizard's scope and mapping steps.</summary>
    public async Task<IssueResult<SourceDescription>> DescribeAsync(Guid importId, CancellationToken ct)
    {
        WorkspaceImport? import;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            import = await db.WorkspaceImports.AsNoTracking().SingleOrDefaultAsync(i => i.Id == importId, ct);
        }

        if (Unusable(import, importId) is { } error)
        {
            return error;
        }

        if (import!.EncryptedToken is null)
        {
            return new IssueError("no_token", "This import reads an uploaded snapshot, not the source.");
        }

        try
        {
            return await sources.Create(import, tokens.Unprotect(import.Id, import.EncryptedToken)).DescribeAsync(ct);
        }
        catch (CryptographicException)
        {
            return new IssueError("token_unreadable", "Kupro can no longer read the stored token. Paste it again.");
        }
        catch (SourceAccessException ex)
        {
            return new IssueError(ex.TokenRejected ? "token_rejected" : "source_unavailable", ex.Message);
        }
        catch (ImportFailedException ex)
        {
            return new IssueError("source_unsupported", ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException)
        {
            return new IssueError("source_unavailable", ImportSources.Unreachable(ex));
        }
    }

    private IssueError? TokenError(string sourceKind) =>
        !tokens.Enabled ? new IssueError("imports_unavailable", "Key storage is not configured on this server, so it can't keep a source token.")
        : !sources.Supports(sourceKind) ? new IssueError("source_unsupported", $"Kupro can't read {ImportDescription.SourceName(sourceKind)} itself. Upload a snapshot instead.")
        : null;

    private static IssueError? Unusable(WorkspaceImport? import, Guid importId) =>
        import is null ? new IssueError("import_not_found", $"No import {importId}.")
        : import.State != ImportState.Active ? new IssueError("import_closed", "This import is finished or undone.")
        : null;
}
