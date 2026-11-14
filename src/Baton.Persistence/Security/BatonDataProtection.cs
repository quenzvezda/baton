using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Baton.Persistence.Security;

/// <summary>
/// The Data Protection key ring every Baton process shares (ADR-0015, ADR-0018; add-operability-and-insight
/// design.md decision 23): application name <c>baton</c>, keys in <c>data_protection_keys</c>, encrypted at rest
/// with the certificate named by <c>Baton:DataProtection:CertificatePath</c> when the deployment provides one.
/// The Control Plane API protects webhook and notification secrets; the orchestrator unprotects notification
/// secrets to deliver notifications. Both must be given the same certificate settings.
/// </summary>
public static class BatonDataProtection
{
    public const string ApplicationName = "baton";

    public static IServiceCollection AddBatonDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDataProtection().SetApplicationName(ApplicationName).PersistKeysToDbContext<BatonDbContext>()
            .ProtectKeysFromConfiguration(configuration);
        services.AddSingleton<NotificationSecrets>();
        return services;
    }
}
