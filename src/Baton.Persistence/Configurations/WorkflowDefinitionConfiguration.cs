using Baton.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Baton.Persistence.Configurations;

internal sealed class WorkflowDefinitionConfiguration : IEntityTypeConfiguration<WorkflowDefinitionEntity>
{
    public void Configure(EntityTypeBuilder<WorkflowDefinitionEntity> builder)
    {
        builder.ToTable("workflow_definitions");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).UseIdentityAlwaysColumn();
        builder.Property(d => d.GraphHash).HasColumnType("character(64)");
        builder.Property(d => d.Graph).HasColumnType("jsonb");
        builder.Property(d => d.ParamsSchema).HasColumnType("jsonb");
        builder.Property(d => d.RegisteredAt).HasDefaultValueSql("now()");
        builder.Property(d => d.IsRetired).HasSuppliedDefault("false");

        builder.HasIndex(d => new { d.Key, d.Version })
            .IsUnique()
            .HasDatabaseName("workflow_definitions_key_version_key");
    }
}
