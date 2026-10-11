using System.ComponentModel.DataAnnotations;
using Flowstate.Api.Features.Activity;
using Flowstate.Api.Features.Issues;
using Flowstate.Api.Features.Workspaces;
using Flowstate.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Flowstate.Api.Features.Comments;

/// <summary>Markdown comment on an issue (task 008). Only its author edits or deletes it.</summary>
public sealed class Comment : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid IssueId { get; set; }
    public Guid AuthorId { get; set; }
    public Surface Via { get; set; }
    public required string Body { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Set on edit; null for a comment never edited.</summary>
    public DateTimeOffset? EditedAt { get; set; }
}

internal sealed class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> builder)
    {
        builder.HasOne<Issue>().WithMany().HasForeignKey(c => c.IssueId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Member>().WithMany().HasForeignKey(c => c.AuthorId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(c => c.Via).HasConversion<string>().HasMaxLength(8);
        builder.Property(c => c.Body).HasMaxLength(CommentRequest.MaxBody);
        builder.HasIndex(c => new { c.WorkspaceId, c.IssueId, c.CreatedAt });
    }
}

/// <param name="Body">Markdown. Images uploaded as attachments are referenced as <c>![name](attachment:ID)</c>.</param>
public sealed record CommentRequest([Required, StringLength(CommentRequest.MaxBody, MinimumLength = 1)] string Body)
{
    public const int MaxBody = 20_000;
}

public sealed record CommentResponse(
    Guid Id, string IssueKey, MemberRef Author, Surface Via, string Body, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt);
