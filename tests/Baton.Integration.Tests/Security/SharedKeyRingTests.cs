using Baton.Integration.Tests.Infrastructure;
using Baton.Persistence;
using Baton.Persistence.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Baton.Integration.Tests.Security;

/// <summary>ADR-0018, M7 design.md decision 23: the orchestrator reads what the API protected.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class SharedKeyRingTests(PostgresFixture pg)
{
    [Fact]
    public async Task Secret_protected_by_the_api_is_read_by_the_orchestrator()
    {
        await using var api = Provider();
        await using var orchestrator = Provider();

        var apiSecrets = api.GetRequiredService<NotificationSecrets>();
        var storedUrl = apiSecrets.ProtectUrl("https://hooks.example.com/services/T0/B0/abc");
        var (secret, storedSecret) = apiSecrets.IssueSecret();

        var orchestratorSecrets = orchestrator.GetRequiredService<NotificationSecrets>();
        Assert.Equal("https://hooks.example.com/services/T0/B0/abc", orchestratorSecrets.RevealUrl(storedUrl));
        Assert.Equal(secret, orchestratorSecrets.RevealSecret(storedSecret));
        Assert.StartsWith("whsec_", secret, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Url_and_secret_use_separate_purposes()
    {
        await using var provider = Provider();
        var secrets = provider.GetRequiredService<NotificationSecrets>();

        Assert.Null(secrets.RevealSecret(secrets.ProtectUrl("https://hooks.example.com/x")));
    }

    private ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBatonPersistence(pg.ConnectionString);
        services.AddBatonDataProtection(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }
}
