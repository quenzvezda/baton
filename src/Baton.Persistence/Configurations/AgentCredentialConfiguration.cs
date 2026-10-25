using Baton.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Baton.Persistence.Configurations;

internal sealed class AgentCredentialConfiguration : IEntityTypeConfiguration<AgentCredentialEntity>
{
    public void Configure(EntityTypeBuilder<AgentCredentialEntity> builder)
    {
        builder.ToTable("agent_credentials");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.CreatedAt).HasDefaultValueSql("now()");

        builder.HasIndex(c => c.TokenHash).IsUnique().HasDatabaseName("agent_credentials_token_hash_key");
        builder.HasIndex(c => new { c.HostGroupId, c.Name }).IsUnique().HasDatabaseName("agent_credentials_host_group_id_name_key");

        // Deleting a group (only allowed without hosts or automations) takes its credentials along.
        builder.HasOne<HostGroupEntity>()
            .WithMany()
            .HasForeignKey(c => c.HostGroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
