using Baton.Domain.Workflows;
using Npgsql;
using NpgsqlTypes;

namespace Baton.Persistence.Definitions;

/// <summary>How many definitions startup registration inserted and how many were already registered.</summary>
public readonly record struct RegistrationSummary(int Inserted, int Unchanged);

/// <summary>
/// WF-04: upserts compiled definitions into <c>workflow_definitions</c> in one transaction at startup.
/// A new <c>key@version</c> is inserted; the same hash is a no-op (only the display name, a label, is
/// refreshed); a different hash rolls everything back and fails startup. Rows are never deleted or
/// given a new graph. Safe when several API replicas start at once: <c>ON CONFLICT</c> serializes them.
/// </summary>
public sealed class WorkflowRegistrar(NpgsqlDataSource dataSource)
{
    private const string UpsertSql = """
        INSERT INTO workflow_definitions (key, version, graph_hash, graph, params_schema, display_name)
        VALUES (@key, @version, @hash, @graph, @paramsSchema, @displayName)
        ON CONFLICT (key, version) DO UPDATE SET display_name = EXCLUDED.display_name
          WHERE workflow_definitions.graph_hash = EXCLUDED.graph_hash
        RETURNING xmax = 0 AS inserted
        """;

    /// <exception cref="WorkflowDefinitionException">A registered <c>key@version</c> has a different graph; nothing was written.</exception>
    public async Task<RegistrationSummary> RegisterAsync(IReadOnlyCollection<CompiledWorkflow> workflows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workflows);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var inserted = 0;
        var unchanged = 0;
        var conflicts = new List<string>();
        foreach (var workflow in workflows)
        {
            await using var cmd = new NpgsqlCommand(UpsertSql, connection, transaction);
            cmd.Parameters.AddWithValue("key", workflow.Key);
            cmd.Parameters.AddWithValue("version", workflow.Version);
            cmd.Parameters.AddWithValue("hash", workflow.Hash);
            cmd.Parameters.AddWithValue("graph", NpgsqlDbType.Jsonb, workflow.CanonicalJson);
            cmd.Parameters.AddWithValue("paramsSchema", NpgsqlDbType.Jsonb, workflow.ParamsSchema);
            cmd.Parameters.AddWithValue("displayName", workflow.DisplayName);

            // No row back: the key@version exists with another hash, so the WHERE blocked the update.
            switch (await cmd.ExecuteScalarAsync(ct))
            {
                case true:
                    inserted++;
                    break;
                case false:
                    unchanged++;
                    break;
                default:
                    conflicts.Add($"{workflow.Id}: already registered with a different graph; bump Version to change it (ADR-0004)");
                    break;
            }
        }

        if (conflicts.Count > 0)
            throw new WorkflowDefinitionException(conflicts);

        await transaction.CommitAsync(ct);
        return new RegistrationSummary(inserted, unchanged);
    }
}
