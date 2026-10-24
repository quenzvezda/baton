using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Baton.Orchestrator.Health;

/// <summary>Healthy when the pooled data source can run a query; exceptions are reported as Unhealthy.</summary>
internal sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
