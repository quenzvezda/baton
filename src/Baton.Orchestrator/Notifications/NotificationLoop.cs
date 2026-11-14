using Baton.Domain.Notifications;
using Baton.Orchestrator.Health;
using Baton.Orchestrator.Loops;
using Baton.Persistence.Notifications;
using Baton.Persistence.Runs;
using Microsoft.Extensions.Options;

namespace Baton.Orchestrator.Notifications;

/// <summary>
/// Every 5 s on every replica (OBS-05, ADR-0018, design.md decision 19): claims up to <see cref="BatchSize"/> due
/// deliveries, sends each one and records the result. A failed attempt waits 1, 5, 15 or 30 minutes
/// (<see cref="NotificationSchedule"/>); the fifth, or a failure that cannot succeed later, gives the delivery up
/// with a <c>notification.failed</c> run event and the failed-notification metric. The run's status never changes
/// (OQ-08).
/// </summary>
public sealed partial class NotificationLoop(
    NotificationOutbox outbox,
    RunQueries runs,
    IEnumerable<INotificationSender> senders,
    IOptions<NotificationPolicy> policy,
    OrchestratorMetrics metrics,
    ILogger<NotificationLoop> logger) : PeriodicLoop(logger)
{
    public const int BatchSize = 20;

    private readonly Dictionary<string, INotificationSender> _senders = senders.ToDictionary(s => s.Kind, StringComparer.Ordinal);

    protected override TimeSpan Interval => TimeSpan.FromSeconds(5);

    protected internal override async Task TickAsync(CancellationToken ct)
    {
        foreach (var delivery in await outbox.ClaimDueAsync(BatchSize, ct))
            await DeliverAsync(delivery, ct);
    }

    private async Task DeliverAsync(DueDelivery delivery, CancellationToken ct)
    {
        SendResult result;
        if (await runs.FindAsync(delivery.RunId, ct) is not { } run)
            result = SendResult.Fail("The run no longer exists.");
        else if (!_senders.TryGetValue(delivery.Kind, out var sender))
            result = SendResult.Fail($"No sender for channel kind '{delivery.Kind}'.");
        else
            result = await sender.SendAsync(delivery, NotificationMessage.From(run, policy.Value.ConsoleBaseUrl), ct);

        if (result.Succeeded)
        {
            if (await outbox.RecordSentAsync(delivery, ct))
                metrics.NotificationSent(delivery.Kind);
            return;
        }

        var error = result.Error ?? "Unknown error.";
        if (result.Retryable && !NotificationSchedule.IsFinal(delivery.Attempt))
        {
            await outbox.RecordRetryAsync(delivery, error, NotificationSchedule.Delay(delivery.Attempt), ct);
            LogRetry(Logger, delivery.DeliveryId, delivery.Kind, delivery.Attempt, error);
            return;
        }

        if (await outbox.RecordFailedAsync(delivery, error, ct))
        {
            metrics.NotificationFailed(delivery.Kind);
            LogFailed(Logger, delivery.DeliveryId, delivery.Kind, delivery.RunId, delivery.Attempt, error);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification {DeliveryId} ({Kind}) attempt {Attempt} failed, will retry: {Error}")]
    private static partial void LogRetry(ILogger logger, string deliveryId, string kind, int attempt, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {DeliveryId} ({Kind}) for run {RunId} given up after {Attempt} attempts: {Error}")]
    private static partial void LogFailed(ILogger logger, string deliveryId, string kind, Guid runId, int attempt, string error);
}

/// <summary>Registration of the notification delivery (OBS-05).</summary>
public static class NotificationServiceCollectionExtensions
{
    public static IServiceCollection AddNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<NotificationPolicy>(configuration.GetSection(NotificationPolicy.Section));
        services.AddOptions<SmtpOptions>()
            .Bind(configuration.GetSection(SmtpOptions.Section))
            .Validate(o => !o.IsConfigured || !string.IsNullOrWhiteSpace(o.From), "Baton:Notifications:Smtp:From is required when Host is set.")
            .Validate(o => Enum.TryParse<MailKit.Security.SecureSocketOptions>(o.Security, ignoreCase: true, out _),
                "Baton:Notifications:Smtp:Security must be StartTls, SslOnConnect, Auto or None.")
            .ValidateOnStart();
        services.AddHttpClient(WebhookNotificationSender.ClientName, client => client.Timeout = WebhookNotificationSender.Timeout)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .ConfigureHttpClient(client => client.DefaultRequestHeaders.UserAgent.ParseAdd($"Baton/{typeof(NotificationLoop).Assembly.GetName().Version}"));
        services.AddSingleton<INotificationSender, WebhookNotificationSender>();
        services.AddSingleton<INotificationSender, SmtpNotificationSender>();
        services.AddHostedService<NotificationLoop>();
        return services;
    }
}
