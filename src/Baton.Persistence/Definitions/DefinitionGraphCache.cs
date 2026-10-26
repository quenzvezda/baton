using System.Collections.Concurrent;
using Baton.Domain.Runs;
using Npgsql;

namespace Baton.Persistence.Definitions;

/// <summary>
/// Registered graphs by hash, read from <c>workflow_definitions.graph</c> once per process (design.md
/// decision 5). A registered graph never changes (ADR-0004), so entries never go stale.
/// </summary>
public sealed class DefinitionGraphCache(NpgsqlDataSource dataSource)
{
    private const string GraphSql = "SELECT graph::text FROM workflow_definitions WHERE graph_hash = @hash LIMIT 1";

    private readonly ConcurrentDictionary<string, DispatchGraph> _graphs = new(StringComparer.Ordinal);

    /// <exception cref="InvalidOperationException">No definition has that hash.</exception>
    public async ValueTask<DispatchGraph> GetAsync(string hash, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hash);
        if (_graphs.TryGetValue(hash, out var cached))
            return cached;

        await using var cmd = dataSource.CreateCommand(GraphSql);
        cmd.Parameters.AddWithValue("hash", hash);
        var json = await cmd.ExecuteScalarAsync(ct) as string
            ?? throw new InvalidOperationException($"No registered workflow definition has graph hash {hash}.");

        return _graphs.GetOrAdd(hash, DispatchGraph.Parse(json));
    }
}
