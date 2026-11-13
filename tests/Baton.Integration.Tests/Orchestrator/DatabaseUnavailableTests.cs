using Baton.Integration.Tests.Infrastructure;
using Baton.Orchestrator.Agents;
using Grpc.Core;
using Npgsql;

namespace Baton.Integration.Tests.Orchestrator;

/// <summary>
/// G1 chaos finding: while Postgres is down, agent calls answer UNAVAILABLE (retried by agents), not UNKNOWN
/// (dropped). Own container, because the test stops the server.
/// </summary>
public sealed class DatabaseUnavailableTests
{
    [Fact]
    public async Task Agent_calls_answer_unavailable_while_postgres_is_down_and_work_again_after()
    {
        var pg = PostgresFixture.Restartable();
        await pg.InitializeAsync();
        try
        {
            await using var orchestrator = await OrchestratorHost.StartAsync(pg);
            var (groupId, group) = await HostData.CreateGroupAsync(pg);
            var (_, token) = await HostData.IssueCredentialAsync(pg, groupId);
            var host = await orchestrator.RegisterAsync(token, HostData.NewName("host"), group);

            await pg.StopServerAsync();
            var error = await Assert.ThrowsAsync<RpcException>(() => orchestrator.HeartbeatAsync(token, host.HostId));
            await pg.StartServerAsync();

            Assert.Equal(StatusCode.Unavailable, error.StatusCode);
            await Eventually.UntilAsync(async () =>
            {
                try
                {
                    await orchestrator.HeartbeatAsync(token, host.HostId);
                    return true;
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
                {
                    return false;
                }
            }, ok => ok, "heartbeats to work again", TimeSpan.FromSeconds(30));
        }
        finally
        {
            await pg.DisposeAsync();
        }
    }

    [Fact]
    public void Sql_errors_are_not_treated_as_an_unavailable_database()
    {
        var uniqueViolation = new PostgresException("duplicate key", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation);
        var adminShutdown = new PostgresException("terminating connection", "FATAL", "FATAL", PostgresErrorCodes.AdminShutdown);

        Assert.False(DatabaseUnavailableInterceptor.IsDatabaseUnavailable(uniqueViolation));
        Assert.True(DatabaseUnavailableInterceptor.IsDatabaseUnavailable(adminShutdown));
        Assert.True(DatabaseUnavailableInterceptor.IsDatabaseUnavailable(new NpgsqlException("Failed to connect", new System.Net.Sockets.SocketException())));
        Assert.False(DatabaseUnavailableInterceptor.IsDatabaseUnavailable(new InvalidOperationException()));
    }
}
