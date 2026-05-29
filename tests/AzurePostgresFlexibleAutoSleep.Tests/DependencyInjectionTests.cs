using AzurePostgresFlexibleAutoSleep.Activity;
using AzurePostgresFlexibleAutoSleep.DependencyInjection;
using AzurePostgresFlexibleAutoSleep.Lifecycle;
using AzurePostgresFlexibleAutoSleep.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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

    [Theory]
    [InlineData(nameof(AzurePostgresAutoSleepOptions.WakePollInterval))]
    [InlineData(nameof(AzurePostgresAutoSleepOptions.StopCheckInterval))]
    [InlineData(nameof(AzurePostgresAutoSleepOptions.StateCacheLifetime))]
    [InlineData(nameof(AzurePostgresAutoSleepOptions.StartupWakeTimeout))]
    [InlineData(nameof(AzurePostgresAutoSleepOptions.ShutdownStopTimeout))]
    public void Options_validation_rejects_non_positive_intervals(string property)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzurePostgresAutoSleep(o =>
        {
            o.ResourceId = "/subscriptions/x/resourceGroups/y/providers/Microsoft.DBforPostgreSQL/flexibleServers/z";
            switch (property)
            {
                case nameof(AzurePostgresAutoSleepOptions.WakePollInterval): o.WakePollInterval = TimeSpan.Zero; break;
                case nameof(AzurePostgresAutoSleepOptions.StopCheckInterval): o.StopCheckInterval = TimeSpan.Zero; break;
                case nameof(AzurePostgresAutoSleepOptions.StateCacheLifetime): o.StateCacheLifetime = TimeSpan.Zero; break;
                case nameof(AzurePostgresAutoSleepOptions.StartupWakeTimeout): o.StartupWakeTimeout = TimeSpan.Zero; break;
                case nameof(AzurePostgresAutoSleepOptions.ShutdownStopTimeout): o.ShutdownStopTimeout = TimeSpan.Zero; break;
            }
        });

        using var sp = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IOptions<AzurePostgresAutoSleepOptions>>().Value);
    }

    [Fact]
    public void WakeOnApplicationStartup_sets_option_flag()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzurePostgresAutoSleep(o =>
        {
            o.ResourceId = "/subscriptions/x/resourceGroups/y/providers/Microsoft.DBforPostgreSQL/flexibleServers/z";
        });
        services.WakeOnApplicationStartup();
        services.Replace(ServiceDescriptor.Singleton<IPostgresLifecycleClient, FakePostgresLifecycleClient>());

        using var sp = services.BuildServiceProvider();
        Assert.True(sp.GetRequiredService<IOptions<AzurePostgresAutoSleepOptions>>().Value.WakeOnStartup);
    }

    [Fact]
    public void StartupWake_is_registered_before_AutoStop()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzurePostgresAutoSleep(o =>
        {
            o.ResourceId = "/subscriptions/x/resourceGroups/y/providers/Microsoft.DBforPostgreSQL/flexibleServers/z";
        });
        services.Replace(ServiceDescriptor.Singleton<IPostgresLifecycleClient, FakePostgresLifecycleClient>());
        services.AddSingleton<IHostApplicationLifetime>(new FakeHostApplicationLifetime());

        using var sp = services.BuildServiceProvider();
        var hosted = sp.GetServices<IHostedService>().ToList();
        var startupIdx = hosted.FindIndex(h => h is StartupWakeHostedService);
        var stopIdx = hosted.FindIndex(h => h is AutoStopHostedService);

        Assert.True(startupIdx >= 0, "StartupWakeHostedService not registered");
        Assert.True(stopIdx >= 0, "AutoStopHostedService not registered");
        Assert.True(startupIdx < stopIdx, "StartupWakeHostedService must precede AutoStopHostedService");
    }

    [Fact]
    public void ShutdownStop_hosted_service_is_registered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzurePostgresAutoSleep(o =>
        {
            o.ResourceId = "/subscriptions/x/resourceGroups/y/providers/Microsoft.DBforPostgreSQL/flexibleServers/z";
        });
        services.Replace(ServiceDescriptor.Singleton<IPostgresLifecycleClient, FakePostgresLifecycleClient>());
        services.AddSingleton<IHostApplicationLifetime>(new FakeHostApplicationLifetime());

        using var sp = services.BuildServiceProvider();
        var hosted = sp.GetServices<IHostedService>().ToList();

        Assert.Contains(hosted, h => h is ShutdownStopHostedService);
    }

    [Fact]
    public void ShutdownStop_resolves_without_revision_provider_registered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzurePostgresAutoSleep(o =>
        {
            o.ResourceId = "/subscriptions/x/resourceGroups/y/providers/Microsoft.DBforPostgreSQL/flexibleServers/z";
        });
        services.Replace(ServiceDescriptor.Singleton<IPostgresLifecycleClient, FakePostgresLifecycleClient>());
        services.AddSingleton<IHostApplicationLifetime>(new FakeHostApplicationLifetime());

        using var sp = services.BuildServiceProvider();
        var hosted = sp.GetServices<IHostedService>().OfType<ShutdownStopHostedService>().Single();

        Assert.NotNull(hosted);
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
