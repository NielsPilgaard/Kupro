using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

[assembly: SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Scope = "namespace",
    Target = "~N:Kupro.Api.Features.Imports", Justification = "An application, not a library: no Visual Basic consumers.")]

namespace Kupro.Api.Features.Imports;

public enum ImportState
{
    Active,
    Finished,
    Undone,
}

public enum ImportRunKind
{
    DryRun,
    Commit,
}

public enum ImportRunState
{
    Queued,
    Running,
    Succeeded,
    Failed,
}

/// <summary>What a source record became in Kupro; undo walks these per kind.</summary>
public enum ImportedEntityKind
{
    Project,
    Member,
    Issue,
    Comment,
    Attachment,
    Label,
    Milestone,
    Sprint,
    Relation,
}

/// <summary>One migration from one source, re-runnable until Finish (ADR-019 IMP-001).</summary>
public sealed class WorkspaceImport : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }

    /// <summary>The snapshot's source kind: <c>jira</c>, <c>azure-devops</c>, <c>csv</c>, <c>kupro</c>...</summary>
    public required string SourceKind { get; set; }

    public string? BaseUrl { get; set; }

    /// <summary>
    /// The source token, encrypted by <see cref="ImportTokens"/>. Deleted on Finish, on Undo and after 90 days without
    /// a run. No endpoint returns it and the workspace export leaves it out.
    /// </summary>
    [JsonIgnore]
    public string? EncryptedToken { get; set; }

    /// <summary>When the source last refused the token. Auto-sync waits until the owner replaces it.</summary>
    public DateTimeOffset? TokenRejectedAt { get; set; }

    /// <summary>JSON: the owner's choices, saved and reused on every run.</summary>
    public string? Mapping { get; set; }

    public ImportState State { get; set; }
    public bool AutoSync { get; set; }

    /// <summary>Where the next delta run reads from; the connector's own format.</summary>
    public string? Cursor { get; set; }

    /// <summary>When Kupro last read the source into the stored snapshot. Null for an import that only takes uploads.</summary>
    public DateTimeOffset? SourceReadAt { get; set; }

    public Guid CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>Last run that changed anything; auto-sync turns itself off after 30 days without one.</summary>
    public DateTimeOffset? LastChangeAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>A dry run or a commit of an import, with what it did.</summary>
public sealed class ImportRun : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ImportId { get; set; }
    public ImportRunKind Kind { get; set; }
    public ImportRunState State { get; set; }

    /// <summary>JSON: created, updated, conflicts and so on, per count.</summary>
    public string? Counts { get; set; }

    /// <summary>JSON: the plan or run report, with sample items.</summary>
    public string? Report { get; set; }

    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// One source record and what it became. Re-runs match on source ID; a commit resumes from these rows after a restart;
/// undo deletes what they list.
/// </summary>
public sealed class ImportedItem : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ImportId { get; set; }

    /// <summary>The kind of source record, e.g. <c>item</c>, <c>person</c> or <c>comment</c>; source IDs are unique within it.</summary>
    public required string SourceKind { get; set; }

    public required string SourceId { get; set; }
    public ImportedEntityKind EntityKind { get; set; }
    public Guid EntityId { get; set; }

    /// <summary>JSON: the last imported value per field. A re-run only updates a field Kupro still holds this value for.</summary>
    public string? LastValues { get; set; }

    /// <summary>High-water mark of the source change IDs already replayed into the feed.</summary>
    public string? SeenChangeMark { get; set; }

    /// <summary>The item's parent in the source. A delta names only what changed, so the fit rebuilds the rest of the tree from these.</summary>
    public string? SourceParent { get; set; }

    /// <summary>The source type's level, for the same reason.</summary>
    public int? Level { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum ImportConflictState
{
    Open,

    /// <summary>The owner kept Kupro's value.</summary>
    Kept,

    /// <summary>The owner took the source's value.</summary>
    UsedSource,

    /// <summary>The two sides came to agree without anyone choosing.</summary>
    Settled,
}

/// <summary>
/// A field that changed on both sides since the last run (ADR-019 Re-runs and conflicts). One row per issue and field,
/// reopened if the source value changes again after it was resolved. Values are JSON, in Kupro's terms.
/// </summary>
public sealed class ImportConflict : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ImportId { get; set; }
    public Guid IssueId { get; set; }

    /// <summary>The field, named as in the activity log: <c>title</c>, <c>state</c>, <c>assignee</c>...</summary>
    public required string Field { get; set; }

    public string? KuproValue { get; set; }
    public string? SourceValue { get; set; }
    public ImportConflictState State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>
/// A source reference that leads to an issue: a key Kupro couldn't keep, a bare <c>#4567</c>, <c>owner/repo#89</c> or a
/// source URL. Issue lookup, search and the palette match these exactly. Kept after Finish.
/// </summary>
public sealed class IssueAlias : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid IssueId { get; set; }
    public Guid ImportId { get; set; }
    public required string Alias { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class ImportConfiguration :
    IEntityTypeConfiguration<WorkspaceImport>, IEntityTypeConfiguration<ImportRun>,
    IEntityTypeConfiguration<ImportedItem>, IEntityTypeConfiguration<IssueAlias>, IEntityTypeConfiguration<ImportConflict>
{
    public void Configure(EntityTypeBuilder<ImportConflict> builder)
    {
        builder.ToTable("import_conflicts");
        builder.HasOne<WorkspaceImport>().WithMany().HasWorkspaceForeignKey(c => c.ImportId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Issue>().WithMany().HasWorkspaceForeignKey(c => c.IssueId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(c => c.Field).HasMaxLength(32);
        builder.Property(c => c.KuproValue).HasColumnType("jsonb");
        builder.Property(c => c.SourceValue).HasColumnType("jsonb");
        builder.Property(c => c.State).HasConversion<string>().HasMaxLength(16);
        builder.HasIndex(c => new { c.WorkspaceId, c.ImportId, c.IssueId, c.Field }).IsUnique();
        builder.HasIndex(c => new { c.WorkspaceId, c.ImportId, c.State });
    }

    public void Configure(EntityTypeBuilder<WorkspaceImport> builder)
    {
        builder.ToTable("imports");
        builder.HasOne<Workspace>().WithMany().HasForeignKey(i => i.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Member>().WithMany().HasWorkspaceForeignKey(i => i.CreatedById).OnDelete(DeleteBehavior.Restrict);
        builder.Property(i => i.SourceKind).HasMaxLength(32);
        builder.Property(i => i.BaseUrl).HasMaxLength(2048);
        builder.Property(i => i.Mapping).HasColumnType("jsonb");
        builder.Property(i => i.State).HasConversion<string>().HasMaxLength(16);
        builder.Property(i => i.Cursor).HasMaxLength(2048);
        builder.HasIndex(i => new { i.WorkspaceId, i.CreatedAt });

        // The hourly scheduler and the token purge look across workspaces; few rows match either.
        builder.HasIndex(i => i.AutoSync).HasFilter("auto_sync");
        builder.HasIndex(i => i.LastRunAt).HasFilter("encrypted_token IS NOT NULL");
    }

    public void Configure(EntityTypeBuilder<ImportRun> builder)
    {
        builder.ToTable("import_runs");
        builder.HasOne<WorkspaceImport>().WithMany().HasWorkspaceForeignKey(r => r.ImportId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(r => r.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.State).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.Counts).HasColumnType("jsonb");
        builder.Property(r => r.Report).HasColumnType("jsonb");
        builder.Property(r => r.Error).HasMaxLength(2000);
        builder.HasIndex(r => new { r.WorkspaceId, r.ImportId, r.CreatedAt });
        builder.HasIndex(r => r.State);
    }

    public void Configure(EntityTypeBuilder<ImportedItem> builder)
    {
        builder.ToTable("imported_items");
        builder.HasOne<WorkspaceImport>().WithMany().HasWorkspaceForeignKey(i => i.ImportId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(i => i.SourceKind).HasMaxLength(32);
        builder.Property(i => i.SourceId).HasMaxLength(512);
        builder.Property(i => i.EntityKind).HasConversion<string>().HasMaxLength(16);
        builder.Property(i => i.LastValues).HasColumnType("jsonb");
        builder.Property(i => i.SeenChangeMark).HasMaxLength(256);
        builder.Property(i => i.SourceParent).HasMaxLength(512);
        builder.HasIndex(i => new { i.WorkspaceId, i.ImportId, i.SourceKind, i.SourceId }).IsUnique();

        // Undo asks whether a record outside this import points at what it created.
        builder.HasIndex(i => new { i.WorkspaceId, i.EntityKind, i.EntityId });
    }

    public void Configure(EntityTypeBuilder<IssueAlias> builder)
    {
        builder.ToTable("issue_aliases");
        builder.HasOne<Issue>().WithMany().HasWorkspaceForeignKey(a => a.IssueId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<WorkspaceImport>().WithMany().HasWorkspaceForeignKey(a => a.ImportId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(a => a.Alias).HasMaxLength(512);
        builder.HasIndex(a => new { a.WorkspaceId, a.Alias }).IsUnique();
        builder.HasIndex(a => new { a.WorkspaceId, a.IssueId });
    }
}
