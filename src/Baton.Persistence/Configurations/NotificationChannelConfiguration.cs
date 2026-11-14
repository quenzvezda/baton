using Baton.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Baton.Persistence.Configurations;

internal sealed class NotificationChannelConfiguration : IEntityTypeConfiguration<NotificationChannelEntity>
{
    public void Configure(EntityTypeBuilder<NotificationChannelEntity> builder)
    {
        builder.ToTable("notification_channels", t =>
        {
            t.HasCheckConstraint("notification_channels_kind_check", "kind IN ('email','webhook')");
            t.HasCheckConstraint("notification_channels_events_check",
                "events <@ ARRAY['failed','timed_out'] AND cardinality(events) > 0");
            t.HasCheckConstraint("notification_channels_email_check", "(kind = 'email') = (email_to IS NOT NULL)");
            t.HasCheckConstraint("notification_channels_webhook_check",
                "(kind = 'webhook') = (webhook_url IS NOT NULL AND webhook_secret IS NOT NULL)");
        });
        builder.HasKey(c => c.Id);

        builder.HasIndex(c => c.AutomationId).HasDatabaseName("ix_notification_channels_automation");

        builder.HasOne<AutomationEntity>()
            .WithMany()
            .HasForeignKey(c => c.AutomationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
