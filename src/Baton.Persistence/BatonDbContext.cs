using System.Text;
using Baton.Persistence.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace Baton.Persistence;

/// <summary>
/// EF Core context for configuration tables. The queue hot path (claim, heartbeat, complete, reaper)
/// does NOT go through EF — see <c>Queue/</c> and ADR-0007. Run, task and history tables are created by
/// raw SQL in the baseline migration and are not part of this model.
/// </summary>
public sealed class BatonDbContext(DbContextOptions<BatonDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<WorkflowDefinitionEntity> WorkflowDefinitions => Set<WorkflowDefinitionEntity>();
    public DbSet<HostGroupEntity> HostGroups => Set<HostGroupEntity>();
    public DbSet<HostEntity> Hosts => Set<HostEntity>();
    public DbSet<AgentCredentialEntity> AgentCredentials => Set<AgentCredentialEntity>();
    public DbSet<AutomationEntity> Automations => Set<AutomationEntity>();
    public DbSet<AutomationTriggerEntity> AutomationTriggers => Set<AutomationTriggerEntity>();
    public DbSet<ConcurrencyLimitEntity> ConcurrencyLimits => Set<ConcurrencyLimitEntity>();

    /// <summary>The Data Protection key ring that encrypts webhook secrets (ADR-0015).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // 0001_baseline.sql has no indexes on foreign-key columns; EF would add one per FK.
        configurationBuilder.Conventions.Remove<ForeignKeyIndexConvention>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BatonDbContext).Assembly);
        ApplyPostgresNaming(modelBuilder);
    }

    /// <summary>
    /// snake_case columns and Postgres' own default constraint names (<c>{table}_pkey</c>,
    /// <c>{table}_{column}_fkey</c>), so the migrated catalog matches <c>0001_baseline.sql</c>.
    /// </summary>
    private static void ApplyPostgresNaming(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();

            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnakeCase(property.Name));

            entity.FindPrimaryKey()?.SetName($"{table}_pkey");

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                var columns = string.Join('_', foreignKey.Properties.Select(p => p.GetColumnName()));
                foreignKey.SetConstraintName($"{table}_{columns}_fkey");
            }
        }
    }

    private static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(name[i]));
        }
        return builder.ToString();
    }
}
