using Baton.Domain.Automations;
using Npgsql;

namespace Baton.Persistence.Queue;

/// <summary>What one trigger-loop pass did.</summary>
/// <param name="Failed">Triggers whose firing threw; each is logged and retried next pass.</param>
public sealed record TriggerPass(int Fired, int Created, int Skipped, IReadOnlyList<(Guid TriggerId, Exception Error)> Failed);

/// <summary>
/// Due cron triggers (ORC-01, AUT-04, design spec §7 "Due cron triggers"), safe on every replica. Each due
/// trigger fires in a transaction of its own that takes the trigger row and its automation row with
/// <c>SKIP LOCKED</c>: another replica, or an Operator editing the automation, makes this pass skip it and the
/// next pass (1 s later) picks it up, so the loop never waits on a lock. The run is created with the idempotency
/// key <c>automation:{id}:trigger:{triggerId}:{fireTime:O}</c>. Which due fire times get a run is the trigger's
/// misfire policy (AUT-08, <see cref="MisfirePlanner"/>): one run per selected time, each under the automation's
/// concurrency policy, and one <c>trigger.misfired</c> event for the times passed over. <c>next_fire_at</c> then
/// moves to the first fire time after <c>now()</c>.
/// </summary>
public sealed class CronTriggers(NpgsqlDataSource dataSource)
{
    public const int BatchSize = 100;

    private const string NextDueSql = """
        SELECT t.id, t.automation_id, t.cron_expr, t.time_zone, t.next_fire_at, now(), t.misfire_policy, t.catch_up_limit
        FROM automation_triggers t
        JOIN automations a ON a.id = t.automation_id
        WHERE t.kind = 'cron' AND t.enabled AND a.enabled AND a.deleted_at IS NULL AND t.next_fire_at <= now()
          AND t.id <> ALL (@failed)
        ORDER BY t.next_fire_at
        LIMIT 1
        FOR UPDATE OF t, a SKIP LOCKED
        """;

    private const string AdvanceSql = """
        UPDATE automation_triggers SET next_fire_at = @next, last_fired_at = COALESCE(@fireTime, last_fired_at) WHERE id = @id
        """;

    public async Task<TriggerPass> FireDueAsync(CancellationToken ct)
    {
        int fired = 0, created = 0, skipped = 0;
        var failed = new List<(Guid, Exception)>();

        while (fired + failed.Count < BatchSize)
        {
            try
            {
                var (triggerId, outcomes) = await FireNextAsync(failed.Select(f => f.Item1).ToArray(), ct);
                if (triggerId is null)
                    break;
                fired++;
                created += outcomes.Count(o => o == FireOutcome.Created);
                skipped += outcomes.Count(o => o == FireOutcome.Skipped);
            }
            catch (TriggerFiringException ex)
            {
                failed.Add((ex.TriggerId, ex.InnerException!));
            }
        }

        return new TriggerPass(fired, created, skipped, failed);
    }

    private async Task<(Guid? TriggerId, IReadOnlyList<FireOutcome> Outcomes)> FireNextAsync(Guid[] failed, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        Guid triggerId, automationId;
        string expression, zone, policy;
        DateTimeOffset nextFireAt, now;
        int? catchUpLimit;
        await using (var due = new NpgsqlCommand(NextDueSql, connection, transaction))
        {
            due.Parameters.AddWithValue("failed", failed);
            await using var reader = await due.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return (null, []);

            (triggerId, automationId) = (reader.GetGuid(0), reader.GetGuid(1));
            (expression, zone) = (reader.GetString(2), reader.GetString(3));
            (nextFireAt, now) = (reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5));
            policy = reader.GetString(6);
            catchUpLimit = reader.IsDBNull(7) ? null : reader.GetInt16(7);
        }

        try
        {
            var plan = MisfirePlanner.Plan(CronSchedule.Create(expression, zone), nextFireAt, now, MisfirePolicies.Parse(policy), catchUpLimit);

            var outcomes = new List<FireOutcome>(plan.Fires.Count);
            foreach (var fireTime in plan.Fires)
            {
                var key = $"automation:{automationId}:trigger:{triggerId}:{fireTime.UtcDateTime:O}";
                var result = await RunCreator.FireAsync(connection, transaction,
                    new FireRequest(automationId, triggerId, AutomationRules.CronKind, key, fireTime), ct);
                outcomes.Add(result.Outcome);
            }

            if (plan.PassedOver > 0)
            {
                // Like run.skipped, no run owns the event, so it is filed under the automation's id.
                await RunEvents.AddAsync(connection, transaction, automationId, null, RunEventTypes.TriggerMisfired, new
                {
                    triggerId,
                    policy,
                    count = plan.PassedOver,
                    first = plan.FirstPassedOver,
                    last = plan.LastPassedOver,
                }, ct);
            }

            await using (var advance = new NpgsqlCommand(AdvanceSql, connection, transaction))
            {
                advance.Parameters.AddWithValue("id", triggerId);
                advance.Parameters.AddWithValue("next", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)plan.Next ?? DBNull.Value);
                advance.Parameters.AddWithValue("fireTime", NpgsqlTypes.NpgsqlDbType.TimestampTz,
                    plan.Fires.Count > 0 ? plan.Fires[^1] : (object)DBNull.Value);
                await advance.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return (triggerId, outcomes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new TriggerFiringException(triggerId, ex);
        }
    }

    private sealed class TriggerFiringException(Guid triggerId, Exception inner)
        : Exception($"Firing trigger {triggerId} failed.", inner)
    {
        public Guid TriggerId { get; } = triggerId;
    }
}
