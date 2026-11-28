using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kupro.Api.Features.Ai;

public enum AiConnectionStatus
{
    /// <summary>Saved, not checked yet.</summary>
    Unverified,

    /// <summary>The last check or call worked.</summary>
    Healthy,

    /// <summary>The vendor refused the key (401 or 403). Calls stop until the key is replaced or checked again.</summary>
    Rejected,
}

/// <summary>
/// A workspace's own AI provider: a provider type, where to reach it and an encrypted key (ADR-022 decision 3).
/// Managed by people with billing access, never by agents.
/// </summary>
public sealed class AiConnection : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }

    /// <summary>An <see cref="AiProviders"/> id.</summary>
    public string ProviderType { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Custom endpoints only: the OpenAI-compatible base URL.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Scaleway only, optional: the project the key belongs to.</summary>
    public string? ProjectId { get; set; }

    /// <summary>Data Protection ciphertext (<see cref="AiKeys"/>); never returned.</summary>
    public string EncryptedKey { get; set; } = "";

    /// <summary>The key's last four characters, so people can tell keys apart.</summary>
    public string KeyHint { get; set; } = "";

    public AiConnectionStatus Status { get; set; }

    /// <summary>The vendor's error from the last failed check or call, verbatim.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>Custom endpoints only: who confirmed the endpoint is in the EU/EEA and run by the workspace or its processor.</summary>
    public Guid? EuConfirmedById { get; set; }

    public DateTimeOffset? EuConfirmedAt { get; set; }

    public Guid CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Which connection serves a capability in this workspace (ADR-022 decision 3). An empty <see cref="Language"/> means every
/// language; a route for one language wins over it. No route means Kupro's server route, within the AI budget.
/// </summary>
public sealed class AiRoute : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public AiCapability Capability { get; set; }

    /// <summary>A meeting language such as <c>da</c>, or empty for every language.</summary>
    public string Language { get; set; } = "";

    public Guid ConnectionId { get; set; }

    /// <summary>Null means the provider type's tested default.</summary>
    public string? Model { get; set; }
}

internal sealed class AiConnectionConfiguration : IEntityTypeConfiguration<AiConnection>, IEntityTypeConfiguration<AiRoute>
{
    public void Configure(EntityTypeBuilder<AiConnection> builder)
    {
        builder.ToTable("ai_connections");
        builder.HasKey(c => c.Id);
        builder.HasOne<Workspace>().WithMany().HasForeignKey(c => c.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => c.WorkspaceId);
        builder.HasOne<Member>().WithMany().HasWorkspaceForeignKey(c => c.CreatedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Member>().WithMany().HasWorkspaceForeignKey(c => c.EuConfirmedById).OnDelete(DeleteBehavior.Restrict);
        builder.Property(c => c.ProviderType).HasMaxLength(32);
        builder.Property(c => c.Name).HasMaxLength(80);
        builder.Property(c => c.Endpoint).HasMaxLength(500);
        builder.Property(c => c.ProjectId).HasMaxLength(64);
        builder.Property(c => c.KeyHint).HasMaxLength(8);
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(c => c.LastError).HasMaxLength(500);
    }

    public void Configure(EntityTypeBuilder<AiRoute> builder)
    {
        builder.ToTable("ai_routes");
        builder.HasKey(r => r.Id);
        builder.HasOne<Workspace>().WithMany().HasForeignKey(r => r.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AiConnection>().WithMany().HasWorkspaceForeignKey(r => r.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(r => new { r.WorkspaceId, r.Capability, r.Language }).IsUnique();
        builder.Property(r => r.Capability).HasConversion<string>().HasMaxLength(32);
        builder.Property(r => r.Language).HasMaxLength(8);
        builder.Property(r => r.Model).HasMaxLength(120);
    }
}
