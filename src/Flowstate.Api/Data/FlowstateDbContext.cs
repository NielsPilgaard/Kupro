using Flowstate.Api.Features.Tokens;
using Flowstate.Api.Features.Users;
using Microsoft.EntityFrameworkCore;

namespace Flowstate.Api.Data;

/// <summary>
/// Single EF Core context for the API. Feature services get it via <see cref="IDbContextFactory{TContext}"/>
/// (ADR-002); entity configurations live next to their feature and are picked up by assembly scan.
/// </summary>
public sealed class FlowstateDbContext(DbContextOptions<FlowstateDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FlowstateDbContext).Assembly);
    }
}
