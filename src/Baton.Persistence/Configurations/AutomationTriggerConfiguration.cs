using Baton.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Baton.Persistence.Configurations;

internal sealed class AutomationTriggerConfiguration : IEntityTypeConfiguration<AutomationTriggerEntity>
{
    public void Configure(EntityTypeBuilder<AutomationTriggerEntity> builder)
    {
        builder.ToTable("automation_triggers", t =>
        {
            t.HasCheckConstraint("automation_triggers_kind_check", "kind IN ('cron','webhook','manual')");
            t.HasCheckConstraint("automation_triggers_misfire_policy_check",
                "misfire_policy IN ('fire_once','skip','catch_up')");
        });
        builder.HasKey(t => t.Id);
        builder.Property(t => t.MisfirePolicy).HasSuppliedDefault("'fire_once'");
        builder.Property(t => t.Enabled).HasSuppliedDefault("true");

        builder.HasIndex(t => t.NextFireAt)
            .HasDatabaseName("ix_triggers_due")
            .HasFilter("enabled AND kind = 'cron'");

        builder.HasOne<AutomationEntity>()
            .WithMany()
            .HasForeignKey(t => t.AutomationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
