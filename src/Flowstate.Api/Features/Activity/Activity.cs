using System.Text.Json;
using Flowstate.Api.Data;
using Flowstate.Api.Features.Issues;
using Flowstate.Api.Features.Workspaces;
using Flowstate.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Flowstate.Api.Features.Activity;

/// <summary>Where an action came from; shown as "via MCP" in the UI (design guide, toasts).</summary>
public enum Surface
{
    Api,
    Web,
    Cli,
    Mcp,
}

/// <summary>
/// Append-only activity log (ADR-008). The actor is a member: for agent tokens the agent member, whose owner is the
/// user it acts for (ADR-001 attribution).
/// </summary>
public sealed class ActivityEntry : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid IssueId { get; set; }
    public Guid ActorId { get; set; }
    public Surface Via { get; set; }

    /// <summary><c>created</c>, <c>updated</c>, <c>relation_added</c>, <c>relation_removed</c>, <c>commented</c>...</summary>
    public required string Kind { get; set; }

    /// <summary>JSON: for <c>updated</c>, <c>{ "field": { "from": ..., "to": ... } }</c>.</summary>
    public string? Data { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class ActivityEntryConfiguration : IEntityTypeConfiguration<ActivityEntry>
{
    public void Configure(EntityTypeBuilder<ActivityEntry> builder)
    {
        builder.ToTable("activity");
        builder.HasOne<Issue>().WithMany().HasForeignKey(a => a.IssueId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Member>().WithMany().HasForeignKey(a => a.ActorId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(a => a.Via).HasConversion<string>().HasMaxLength(8);
        builder.Property(a => a.Kind).HasMaxLength(32);
        builder.Property(a => a.Data).HasColumnType("jsonb");
        builder.HasIndex(a => new { a.WorkspaceId, a.IssueId, a.CreatedAt });
    }
}

/// <summary>One field change in an <c>updated</c> entry.</summary>
public sealed record FieldChange(object? From, object? To);

/// <summary>
/// Adds activity entries to the caller's DbContext so they commit in the same SaveChanges as the change itself.
/// Only feature services that own the changed entity call this.
/// </summary>
public sealed class ActivityLog(WorkspaceContext workspace, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Record(FlowstateDbContext db, Guid issueId, string kind, object? data = null) =>
        db.Activity.Add(new ActivityEntry
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.RequireWorkspaceId(),
            IssueId = issueId,
            ActorId = workspace.MemberId,
            Via = workspace.Surface,
            Kind = kind,
            Data = data is null ? null : JsonSerializer.Serialize(data, Json),
            CreatedAt = clock.GetUtcNow(),
        });
}
