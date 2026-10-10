using Microsoft.EntityFrameworkCore;

namespace Flowstate.Api.Data;

/// <summary>
/// Single EF Core context for the API. Feature services get it via <see cref="IDbContextFactory{TContext}"/>
/// (ADR-002); entity configurations live next to their feature and are picked up by assembly scan.
/// </summary>
public sealed class FlowstateDbContext(DbContextOptions<FlowstateDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FlowstateDbContext).Assembly);
    }
}
