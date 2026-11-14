using Baton.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Baton.Persistence.Configurations;

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntryEntity>
{
    public void Configure(EntityTypeBuilder<AuditEntryEntity> builder)
    {
        builder.ToTable("audit_log", t => t.HasCheckConstraint("audit_log_entity_type_check",
            "entity_type IN ('automation','host_group','agent_credential','workflow_limit')"));
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).UseIdentityAlwaysColumn();
        builder.Property(a => a.At).HasDefaultValueSql("now()");
        builder.Property(a => a.Before).HasColumnType("jsonb");
        builder.Property(a => a.After).HasColumnType("jsonb");

        // No FK: entries outlive their entity (spec "Automation deleted").
        builder.HasIndex(a => new { a.EntityType, a.EntityId, a.At, a.Id })
            .IsDescending(false, false, true, true)
            .HasDatabaseName("ix_audit_entity");
        builder.HasIndex(a => a.At).HasDatabaseName("ix_audit_at");
    }
}
