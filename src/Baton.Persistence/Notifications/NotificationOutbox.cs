using Baton.Persistence.Queue;
using Npgsql;

namespace Baton.Persistence.Notifications;

/// <summary>A delivery claimed for one attempt, with its channel.</summary>
/// <param name="Attempt">This attempt's number, 1-based; it guards the result write.</param>
public sealed record DueDelivery(
    long Id,
    Guid RunId,
    Guid ChannelId,
    string Event,
    int Attempt,
    string Kind,
    IReadOnlyList<string>? EmailTo,
    byte[]? WebhookUrl,
    byte[]? WebhookSecret)
{
    /// <summary>The opaque id receivers deduplicate on; the same on every attempt of the delivery.</summary>
    public string DeliveryId => $"ntf_{Id}";
}

/// <summary>
/// The notification outbox (ADR-0018, add-operability-and-insight design.md decisions 17–19). The finalizer writes
/// rows; any orchestrator replica claims due ones with <c>SKIP LOCKED</c> and marks them with a short
/// <c>locked_until</c> in a transaction of its own, sends outside any transaction (no row lock across network
/// I/O), and records the result in another, guarded by the attempt number so a late result never overwrites a
/// newer attempt. A replica that dies mid-send leaves the row locked until <c>locked_until</c>, after which another
/// replica sends it again: at least once.
/// </summary>
public sealed class NotificationOutbox(NpgsqlDataSource dataSource)
{
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(2);

    private const string ClaimSql = """
        WITH due AS (
          SELECT id FROM notification_deliveries
          WHERE status = 'pending' AND next_attempt_at <= now() AND (locked_until IS NULL OR locked_until < now())
          ORDER BY next_attempt_at
          LIMIT @limit
          FOR UPDATE SKIP LOCKED)
        UPDATE notification_deliveries d SET locked_until = now() + @lock, attempts = d.attempts + 1
        FROM due, notification_channels c
        WHERE d.id = due.id AND c.id = d.channel_id
        RETURNING d.id, d.workflow_run_id, d.channel_id, d.event, d.attempts, c.kind, c.email_to, c.webhook_url, c.webhook_secret
        """;

    private const string SentSql = """
        UPDATE notification_deliveries SET status = 'sent', sent_at = now(), locked_until = NULL, last_error = NULL
        WHERE id = @id AND attempts = @attempt AND status = 'pending'
        """;

    private const string RetrySql = """
        UPDATE notification_deliveries SET next_attempt_at = now() + @delay, locked_until = NULL, last_error = @error
        WHERE id = @id AND attempts = @attempt AND status = 'pending'
        """;

    private const string FailedSql = """
        UPDATE notification_deliveries SET status = 'failed', locked_until = NULL, last_error = @error
        WHERE id = @id AND attempts = @attempt AND status = 'pending'
        """;

    /// <summary>Claims up to <paramref name="limit"/> due deliveries for one attempt each.</summary>
    public async Task<IReadOnlyList<DueDelivery>> ClaimDueAsync(int limit, CancellationToken ct)
    {
        var due = new List<DueDelivery>();
        await using var cmd = dataSource.CreateCommand(ClaimSql);
        cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.AddWithValue("lock", LockDuration);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            due.Add(new DueDelivery(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<string[]>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<byte[]>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<byte[]>(8)));
        }

        return due;
    }

    /// <returns>Whether this attempt's result was recorded (false when a newer attempt owns the row).</returns>
    public Task<bool> RecordSentAsync(DueDelivery delivery, CancellationToken ct) =>
        RecordAsync(delivery, SentSql, RunEventTypes.NotificationSent, null, null, ct);

    /// <summary>A failed attempt that will be tried again after <paramref name="delay"/>.</summary>
    public Task<bool> RecordRetryAsync(DueDelivery delivery, string error, TimeSpan delay, CancellationToken ct) =>
        RecordAsync(delivery, RetrySql, null, error, delay, ct);

    /// <summary>The delivery is given up: recorded on the run's timeline, never on its status (OQ-08).</summary>
    public Task<bool> RecordFailedAsync(DueDelivery delivery, string error, CancellationToken ct) =>
        RecordAsync(delivery, FailedSql, RunEventTypes.NotificationFailed, error, null, ct);

    private async Task<bool> RecordAsync(
        DueDelivery delivery, string sql, string? eventType, string? error, TimeSpan? delay, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(sql, connection, transaction))
        {
            cmd.Parameters.AddWithValue("id", delivery.Id);
            cmd.Parameters.AddWithValue("attempt", delivery.Attempt);
            if (error is not null)
                cmd.Parameters.AddWithValue("error", error);
            if (delay is { } wait)
                cmd.Parameters.AddWithValue("delay", wait);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                return false;
        }

        if (eventType is not null)
        {
            await RunEvents.AddAsync(connection, transaction, delivery.RunId, null, eventType, new
            {
                deliveryId = delivery.DeliveryId,
                channelId = delivery.ChannelId,
                kind = delivery.Kind,
                attempts = delivery.Attempt,
                error,
            }, ct);
        }

        await transaction.CommitAsync(ct);
        return true;
    }
}
