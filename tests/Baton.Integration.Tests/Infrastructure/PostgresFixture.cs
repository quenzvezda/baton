using Baton.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Baton.Integration.Tests.Infrastructure;

/// <summary>One Postgres 16 container per test collection, migrated once to the latest schema.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container;

    public PostgresFixture()
        : this(new PostgreSqlBuilder("postgres:16-alpine").Build())
    {
    }

    private readonly bool _ownsContainer = true;

    private PostgresFixture(PostgreSqlContainer container) => _container = container;

    private PostgresFixture(PostgreSqlContainer container, string connectionString)
    {
        _container = container;
        _ownsContainer = false;
        ConnectionString = connectionString;
        DataSource = NpgsqlDataSource.Create(connectionString);
    }

    /// <summary>
    /// A container on a fixed host port, so <see cref="RestartAsync"/> keeps the connection string valid, with room
    /// for several orchestrator replicas' pools (G1 load and chaos tests). Call <see cref="InitializeAsync"/> yourself.
    /// </summary>
    public static PostgresFixture Restartable()
    {
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }

        return new PostgresFixture(new PostgreSqlBuilder("postgres:16-alpine")
            .WithPortBinding(port, PostgreSqlBuilder.PostgreSqlPort)
            .WithCommand("-c", "max_connections=400")
            .Build());
    }

    /// <summary>Stops and starts the server: every open connection breaks, data and address stay.</summary>
    public async Task RestartAsync(TimeSpan downtime)
    {
        await StopServerAsync();
        await Task.Delay(downtime);
        await StartServerAsync();
    }

    /// <summary>Stops the server (only for <see cref="Restartable"/> fixtures, whose address survives).</summary>
    public Task StopServerAsync() => _container.StopAsync();

    public Task StartServerAsync() => _container.StartAsync();

    public string ConnectionString { get; private set; } = null!;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        DataSource = NpgsqlDataSource.Create(ConnectionString);

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    /// <summary>Another database on the same server (e.g. a restored backup); disposing it leaves the server running.</summary>
    public PostgresFixture ForDatabase(string database) =>
        new(_container, new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString);

    /// <summary>Runs a command inside the server's container (e.g. <c>pg_dump</c>); fails on a non-zero exit code.</summary>
    public async Task<string> ExecAsync(params string[] command)
    {
        var result = await _container.ExecAsync(command);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"`{string.Join(' ', command)}` exited with {result.ExitCode}: {result.Stderr}");
        return result.Stdout;
    }

    public BatonDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<BatonDbContext>().UseNpgsql(DataSource).Options);

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        if (_ownsContainer)
            await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
