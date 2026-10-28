using Baton.Persistence.Definitions;
using Baton.Persistence.Hosts;
using Baton.Persistence.Queue;
using Baton.Persistence.Runs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Baton.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Service key of the non-pooled <see cref="NpgsqlDataSource"/> used for <c>LISTEN</c>.</summary>
    public const string ListenDataSourceKey = "listen";

    /// <summary>
    /// Registers one pooled <see cref="NpgsqlDataSource"/> per process and <see cref="BatonDbContext"/>
    /// on top of it, so EF and the hand-written queue SQL share the same pool.
    /// </summary>
    public static IServiceCollection AddBatonPersistence(this IServiceCollection services, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Connection string 'Baton' is not configured.");

        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddDbContext<BatonDbContext>((sp, options) =>
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<WorkflowRegistrar>();
        services.AddSingleton<AgentRegistry>();
        services.AddSingleton<HostDirectory>();
        services.AddSingleton<DefinitionGraphCache>();
        services.AddSingleton<RunMaterializer>();
        services.AddSingleton<TaskClaims>();
        services.AddSingleton<TaskLeases>();
        services.AddSingleton<LeaseReaper>();
        services.AddSingleton<RetryPromoter>();
        services.AddSingleton<RunCreator>();
        services.AddSingleton<RunCancellation>();
        services.AddSingleton<CronTriggers>();
        services.AddSingleton<RunAdmission>();
        services.AddSingleton<RunQueries>();
        services.AddSingleton<TaskLogReader>();
        return services;
    }

    /// <summary>
    /// Registers a keyed (<see cref="ListenDataSourceKey"/>), non-pooled <see cref="NpgsqlDataSource"/>
    /// for <c>LISTEN</c>. Subscriptions belong to a session, so they need a dedicated connection that
    /// bypasses the pool and PgBouncer transaction mode. <c>Pooling=false</c> is forced.
    /// </summary>
    public static IServiceCollection AddBatonListenDataSource(this IServiceCollection services, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Connection string 'BatonListen' is not configured.");

        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        services.AddKeyedSingleton(ListenDataSourceKey, (_, _) => NpgsqlDataSource.Create(builder.ConnectionString));
        return services;
    }
}
