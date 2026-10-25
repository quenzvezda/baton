using Baton.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Baton.Persistence.Configurations;

internal sealed class HostConfiguration : IEntityTypeConfiguration<HostEntity>
{
    public void Configure(EntityTypeBuilder<HostEntity> builder)
    {
        builder.ToTable("hosts", t =>
        {
            t.HasCheckConstraint("hosts_capacity_slots_check", "capacity_slots > 0");
            t.HasCheckConstraint("hosts_status_check", "status IN ('online','draining','offline')");
        });
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Labels).HasSuppliedDefault("'{}'");
        builder.Property(h => h.RegisteredAt).HasDefaultValueSql("now()");
        builder.Property(h => h.DrainRequested).HasSuppliedDefault("false");

        builder.HasIndex(h => h.Name).IsUnique().HasDatabaseName("hosts_name_key");

        builder.HasOne<HostGroupEntity>()
            .WithMany()
            .HasForeignKey(h => h.HostGroupId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<AgentCredentialEntity>()
            .WithMany()
            .HasForeignKey(h => h.CredentialId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
