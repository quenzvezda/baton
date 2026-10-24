using Baton.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Baton.Persistence.Configurations;

internal sealed class ConcurrencyLimitConfiguration : IEntityTypeConfiguration<ConcurrencyLimitEntity>
{
    public void Configure(EntityTypeBuilder<ConcurrencyLimitEntity> builder)
    {
        builder.ToTable("concurrency_limits", t =>
            t.HasCheckConstraint("concurrency_limits_in_use_check", "in_use >= 0"));
        builder.HasKey(l => l.Key);
        builder.Property(l => l.InUse).HasSuppliedDefault("0");
    }
}
