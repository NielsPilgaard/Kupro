using System.Linq.Expressions;
using Flowstate.Api.Common;
using Flowstate.Api.Features.Activity;
using Flowstate.Api.Features.Issues;
using Flowstate.Api.Features.Projects;
using Flowstate.Api.Features.Tokens;
using Flowstate.Api.Features.Users;
using Flowstate.Api.Features.Workspaces;
using Flowstate.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Flowstate.Api.Data;

/// <summary>
/// Single EF Core context for the API. Feature services get it via <see cref="IDbContextFactory{TContext}"/>
/// (ADR-002); entity configurations live next to their feature and are picked up by assembly scan.
/// Every <see cref="ITenantOwned"/> entity is filtered to the request's workspace (ADR-009).
/// </summary>
public sealed class FlowstateDbContext(DbContextOptions<FlowstateDbContext> options, WorkspaceContext? workspace = null)
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
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<IssueLabel> IssueLabels => Set<IssueLabel>();
    public DbSet<IssueRelation> IssueRelations => Set<IssueRelation>();
    public DbSet<ActivityEntry> Activity => Set<ActivityEntry>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    /// <summary>
    /// Workspace of the current request; null outside a workspace route, which makes every tenant-owned query empty
    /// rather than unfiltered.
    /// </summary>
    public Guid? CurrentWorkspaceId => workspace?.WorkspaceId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FlowstateDbContext).Assembly);

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
