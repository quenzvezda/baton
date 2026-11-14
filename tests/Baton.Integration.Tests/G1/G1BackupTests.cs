using System.Net;
using System.Net.Http.Json;
using System.Text;
using Baton.ControlPlane.Api.Automations;
using Baton.Domain.Automations;
using Baton.Integration.Tests.Automations;
using Baton.Integration.Tests.Infrastructure;
using Npgsql;
using Xunit.Abstractions;

namespace Baton.Integration.Tests.G1;

/// <summary>
/// Gate G1 Operations: a <c>pg_dump</c> backup restores into an empty database with every table identical, the
/// history partitions intact, and the Data Protection key ring usable — a webhook signed with a secret created
/// before the backup is accepted by an API running on the restored database. Procedure:
/// <c>docs/runbooks/backup-restore.md</c>.
/// </summary>
public sealed class G1BackupTests(ITestOutputHelper output)
{
    private const string Restored = "baton_restored";

    [Fact]
    [Trait("Category", "G1")]
    public async Task Backup_restores_into_an_empty_database_with_identical_data_and_usable_secrets()
    {
        var pg = new PostgresFixture();
        await pg.InitializeAsync();
        try
        {
            await BackupAndRestoreAsync(pg);
        }
        finally
        {
            await pg.DisposeAsync();
        }
    }

    private async Task BackupAndRestoreAsync(PostgresFixture pg)
    {
        var user = new NpgsqlConnectionStringBuilder(pg.ConnectionString).Username!;
        var database = new NpgsqlConnectionStringBuilder(pg.ConnectionString).Database!;

        // Configuration, a finished run with history, and a webhook trigger whose secret is protected by the key ring.
        string secret;
        Guid triggerId, automationId;
        await using (var api = await AutomationApi.StartAsync(pg))
        {
            var (automation, _) = await api.CreateAsync(api.Request(triggers: [new TriggerRequest(null, "webhook")], policy: "allow", maxRuns: 10));
            (automationId, triggerId, secret) = (automation.Id, automation.Triggers.Single().Id, automation.Triggers.Single().WebhookSecret!);
            await api.RunNowAsync(automation.Id);
        }

        var s = await RunScenario.CreateAsync(pg);
        var host = await s.Dispatch.AddHostAsync();
        var finished = await s.StartRunAsync();
        await s.CompleteRunAsync(finished, host);
        Assert.Equal("succeeded", await s.RunStatusAsync(finished));

        // Backup and restore, as in the runbook.
        await pg.ExecAsync("pg_dump", "-U", user, "-d", database, "--format=custom", "--file=/tmp/baton.dump");
        await pg.ExecAsync("createdb", "-U", user, Restored);
        await pg.ExecAsync("pg_restore", "-U", user, "-d", Restored, "--no-owner", "--exit-on-error", "/tmp/baton.dump");

        var restored = pg.ForDatabase(Restored);
        try
        {
            await CompareAsync(pg, restored, triggerId, automationId, secret);
        }
        finally
        {
            await restored.DisposeAsync();
        }
    }

    private async Task CompareAsync(PostgresFixture pg, PostgresFixture restored, Guid triggerId, Guid automationId, string secret)
    {
        var original = await FingerprintAsync(pg);
        var copy = await FingerprintAsync(restored);
        output.WriteLine($"{original.Count} tables compared; rows: {string.Join(", ", original.Where(t => t.Value.Rows > 0).Select(t => $"{t.Key} {t.Value.Rows}"))}");

        Assert.True(original.Count > 20, $"Only {original.Count} tables found.");
        Assert.Equal(original, copy);
        Assert.Equal(
            await HostData.ScalarAsync<long>(pg, "SELECT last_value FROM lease_token_seq"),
            await HostData.ScalarAsync<long>(restored, "SELECT last_value FROM lease_token_seq"));
        Assert.Equal(0, await HostData.ScalarAsync<int>(restored, "SELECT ensure_history_partitions()")); // partitions came back

        // The restored key ring still decrypts the webhook secret: a signed delivery creates a run there.
        await using (var api = await ControlPlaneHost.StartAsync(restored))
        {
            using var sender = api.Client();
            using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{triggerId}")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            message.Headers.TryAddWithoutValidation(WebhookSignature.HeaderName,
                WebhookSignature.Compute(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "after-restore", Encoding.UTF8.GetBytes("{}")));
            message.Headers.Add(WebhookEndpoints.DeliveryIdHeader, "after-restore");
            var response = await sender.SendAsync(message);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var runId = (await response.Content.ReadFromJsonAsync<RunAccepted>())!.RunId;
            Assert.Equal(automationId, await HostData.ScalarAsync<Guid>(restored, "SELECT automation_id FROM workflow_runs WHERE id = @id", ("id", runId)));
        }
    }

    /// <summary>Row count and an order-independent checksum of every table (leaf partitions included).</summary>
    private static async Task<Dictionary<string, (long Rows, string Checksum)>> FingerprintAsync(PostgresFixture db)
    {
        var tables = new List<string>();
        await using (var cmd = db.DataSource.CreateCommand("""
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind = 'r' ORDER BY 1
            """))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                tables.Add(reader.GetString(0));
        }

        var result = new Dictionary<string, (long, string)>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            await using var cmd = db.DataSource.CreateCommand(
                $"SELECT count(*), COALESCE(md5(string_agg(md5(t::text), '' ORDER BY md5(t::text))), '') FROM \"{table}\" t");
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            result[table] = (reader.GetInt64(0), reader.GetString(1));
        }

        return result;
    }
}
