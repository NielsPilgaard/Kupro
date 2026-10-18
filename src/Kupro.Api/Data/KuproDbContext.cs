using System.Linq.Expressions;
using Kupro.Api.Common;
using Kupro.Api.Features.Activity;
using Kupro.Api.Features.Attachments;
using Kupro.Api.Features.Comments;
using Kupro.Api.Features.Exports;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Meetings;
using Kupro.Api.Features.Plan;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Sprints;
using Kupro.Api.Features.Tokens;
using Kupro.Api.Features.Users;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Api.Data;

/// <summary>
/// Single EF Core context for the API. Feature services get it via <see cref="IDbContextFactory{TContext}"/>
/// (ADR-002); entity configurations live next to their feature and are picked up by assembly scan.
/// Every <see cref="ITenantOwned"/> entity is filtered to the request's workspace (ADR-009).
/// </summary>
public sealed class KuproDbContext(DbContextOptions<KuproDbContext> options, WorkspaceContext? workspace = null)
    : DbContext(options)
{
    /// <summary>Name of the global tenant filter, for the rare commented <c>IgnoreQueryFilters</c>.</summary>
    public const string TenantFilter = "tenant";

    public DbSet<User> Users => Set<User>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Issue> Issues => Set<Issue>();
    public DbSet<Sprint> Sprints => Set<Sprint>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<ProjectCapacity> ProjectCapacity => Set<ProjectCapacity>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<IssueLabel> IssueLabels => Set<IssueLabel>();
    public DbSet<IssueRelation> IssueRelations => Set<IssueRelation>();
    public DbSet<ActivityEntry> Activity => Set<ActivityEntry>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<MeetingSession> Meetings => Set<MeetingSession>();
    public DbSet<TranscriptSegment> TranscriptSegments => Set<TranscriptSegment>();
    public DbSet<MeetingProposal> MeetingProposals => Set<MeetingProposal>();
    public DbSet<MeetingSettings> MeetingSettings => Set<MeetingSettings>();
    public DbSet<WorkspaceExport> WorkspaceExports => Set<WorkspaceExport>();

    /// <summary>
    /// Workspace of the current request; null outside a workspace route, which makes every tenant-owned query empty
    /// rather than unfiltered.
    /// </summary>
    public Guid? CurrentWorkspaceId => workspace?.WorkspaceId;

    /// <summary>
    /// pg_trgm <c>word_similarity</c> (0-1): how well <paramref name="text"/> matches the best run of words in
    /// <paramref name="target"/>. Typo tolerant, so "dependancy" finds "Render dependency curves".
    /// </summary>
    [DbFunction("word_similarity", IsBuiltIn = true)]
    public static float WordSimilarity(string text, string target) => throw new NotSupportedException();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pg_trgm");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(KuproDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)))
        {
            // e => (Guid?)e.WorkspaceId == this.CurrentWorkspaceId; EF re-reads the property per query.
            var e = Expression.Parameter(entityType.ClrType, "e");
            var filter = Expression.Lambda(
                Expression.Equal(
                    Expression.Convert(Expression.Property(e, nameof(ITenantOwned.WorkspaceId)), typeof(Guid?)),
                    Expression.Property(Expression.Constant(this), nameof(CurrentWorkspaceId))),
                e);
            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(TenantFilter, filter);
        }
    }
}
