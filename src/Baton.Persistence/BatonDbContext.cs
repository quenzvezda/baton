using Microsoft.EntityFrameworkCore;

namespace Baton.Persistence;

/// <summary>
/// EF Core context for configuration and history tables. The queue hot path (claim, heartbeat,
/// complete, reaper) does NOT go through EF — see <c>Queue/</c> and ADR-0007.
/// Entity mappings and the initial migration land in M0 (openspec/changes/bootstrap-foundation).
/// </summary>
public sealed class BatonDbContext(DbContextOptions<BatonDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BatonDbContext).Assembly);
    }
}
