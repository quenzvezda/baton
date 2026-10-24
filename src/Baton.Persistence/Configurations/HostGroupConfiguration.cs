using Baton.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Baton.Persistence.Configurations;

internal sealed class HostGroupConfiguration : IEntityTypeConfiguration<HostGroupEntity>
{
    public void Configure(EntityTypeBuilder<HostGroupEntity> builder)
    {
        builder.ToTable("host_groups");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.LeaseSeconds).HasSuppliedDefault("60");
        builder.Property(g => g.DispatchPaused).HasSuppliedDefault("false");

        builder.HasIndex(g => g.Name).IsUnique().HasDatabaseName("host_groups_name_key");
    }
}
