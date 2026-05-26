using AzurePostgresFlexibleAutoSleep.Activity;
using AzurePostgresFlexibleAutoSleep.DependencyInjection;
using AzurePostgresFlexibleAutoSleep.Lifecycle;
using AzurePostgresFlexibleAutoSleep.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzurePostgresFlexibleAutoSleep.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddAzurePostgresAutoSleep_registers_public_services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzurePostgresAutoSleep(o =>
        {
            o.ResourceId = "/subscriptions/x/resourceGroups/y/providers/Microsoft.DBforPostgreSQL/flexibleServers/z";
        });
        services.Replace(ServiceDescriptor.Singleton<IPostgresLifecycleClient, FakePostgresLifecycleClient>());

        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<IDbActivityTracker>());
        Assert.NotNull(sp.GetRequiredService<IDbWaker>());
        Assert.NotNull(sp.GetRequiredService<IPostgresLifecycleClient>());
        Assert.NotNull(sp.GetRequiredService<ActivityCommandInterceptor>());
        Assert.NotNull(sp.GetRequiredService<IOptions<AzurePostgresAutoSleepOptions>>().Value);
    }

    [Fact]
    public void Options_validation_rejects_blank_resource_id()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzurePostgresAutoSleep(o => { o.ResourceId = " "; });

        using var sp = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IOptions<AzurePostgresAutoSleepOptions>>().Value);
    }
}
