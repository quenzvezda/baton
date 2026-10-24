using Baton.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Baton.Persistence.Configurations;

internal sealed class AutomationConfiguration : IEntityTypeConfiguration<AutomationEntity>
{
    public void Configure(EntityTypeBuilder<AutomationEntity> builder)
    {
        builder.ToTable("automations", t =>
        {
            t.HasCheckConstraint("automations_priority_check", "priority BETWEEN 0 AND 100");
            t.HasCheckConstraint("automations_concurrency_policy_check",
                "concurrency_policy IN ('skip','queue','replace','allow')");
            t.HasCheckConstraint("automations_placement_mode_check",
                "placement_mode IN ('per_task','pinned_run')");
        });
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Enabled).HasSuppliedDefault("true");
        builder.Property(a => a.Parameters).HasColumnType("jsonb").HasSuppliedDefault("'{}'");
        builder.Property(a => a.RequiredLabels).HasSuppliedDefault("'{}'");
        builder.Property(a => a.Priority).HasSuppliedDefault("50");
        builder.Property(a => a.MaxConcurrentRuns).HasSuppliedDefault("1");
        builder.Property(a => a.ConcurrencyPolicy).HasSuppliedDefault("'skip'");
        builder.Property(a => a.PlacementMode).HasSuppliedDefault("'per_task'");
        builder.Property(a => a.UpdatedAt).HasDefaultValueSql("now()");
        builder.Property(a => a.RowVersion).HasSuppliedDefault("0").IsConcurrencyToken();

        builder.HasIndex(a => a.Name).IsUnique().HasDatabaseName("automations_name_key");

        builder.HasOne<HostGroupEntity>()
            .WithMany()
            .HasForeignKey(a => a.HostGroupId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
