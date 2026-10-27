using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Baton.Persistence.Configurations;

/// <summary>Data Protection's key ring, shared by every Control Plane replica (ADR-0015).</summary>
internal sealed class DataProtectionKeyConfiguration : IEntityTypeConfiguration<DataProtectionKey>
{
    public void Configure(EntityTypeBuilder<DataProtectionKey> builder)
    {
        builder.ToTable("data_protection_keys");
        builder.HasKey(k => k.Id);
    }
}
