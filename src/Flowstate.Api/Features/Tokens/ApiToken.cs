using Flowstate.Api.Features.Users;
using Flowstate.Api.Features.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Flowstate.Api.Features.Tokens;

public enum ApiTokenKind
{
    /// <summary>Acts as the user (<c>fs_pat_</c>).</summary>
    Personal,

    /// <summary>An agent acting on behalf of the user; actions are attributed to the agent (<c>fs_agent_</c>).</summary>
    Agent,
}

/// <summary>Headless credential for CLI, CI and agents (ADR-005). Only the SHA-256 hash of the secret is stored.</summary>
public sealed class ApiToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Workspace the token is limited to; null for a personal token valid in all the user's workspaces.</summary>
    public Guid? WorkspaceId { get; set; }

    public ApiTokenKind Kind { get; set; }
    public required string Name { get; set; }
    public string? AgentName { get; set; }
    public required byte[] Hash { get; set; }

    /// <summary>Prefix plus last four characters, so users can recognise a token without the secret.</summary>
    public required string Hint { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

internal sealed class ApiTokenConfiguration : IEntityTypeConfiguration<ApiToken>
{
    public void Configure(EntityTypeBuilder<ApiToken> builder)
    {
        builder.HasIndex(t => t.Hash).IsUnique();
        builder.HasIndex(t => t.UserId);
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Workspace>().WithMany().HasForeignKey(t => t.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(t => t.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(t => t.Name).HasMaxLength(100);
        builder.Property(t => t.AgentName).HasMaxLength(64);
        builder.Property(t => t.Hint).HasMaxLength(32);
    }
}
