using AzurePostgresFlexibleAutoSleep.Activity;
using AzurePostgresFlexibleAutoSleep.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace AzurePostgresFlexibleAutoSleep.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAzurePostgresAutoSleep(
        this IServiceCollection services,
        Action<AzurePostgresAutoSleepOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<AzurePostgresAutoSleepOptions>()
            .Configure(configure)
            .Validate(
                opts => !string.IsNullOrWhiteSpace(opts.ResourceId),
                "AzurePostgresAutoSleepOptions.ResourceId must be set.")
            .Validate(
                opts => opts.IdleThreshold > TimeSpan.Zero,
                "AzurePostgresAutoSleepOptions.IdleThreshold must be positive.")
            .Validate(
                opts => opts.WakeTimeout > TimeSpan.Zero,
                "AzurePostgresAutoSleepOptions.WakeTimeout must be positive.")
            .Validate(
                opts => opts.WakePollInterval > TimeSpan.Zero,
                "AzurePostgresAutoSleepOptions.WakePollInterval must be positive.")
            .Validate(
                opts => opts.StopCheckInterval > TimeSpan.Zero,
                "AzurePostgresAutoSleepOptions.StopCheckInterval must be positive.")
            .Validate(
                opts => opts.StateCacheLifetime > TimeSpan.Zero,
                "AzurePostgresAutoSleepOptions.StateCacheLifetime must be positive.")
            .Validate(
                opts => opts.StartupWakeTimeout > TimeSpan.Zero,
                "AzurePostgresAutoSleepOptions.StartupWakeTimeout must be positive.")
            .Validate(
                opts => opts.ShutdownStopTimeout > TimeSpan.Zero,
                "AzurePostgresAutoSleepOptions.ShutdownStopTimeout must be positive.");

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDbActivityTracker, DbActivityTracker>();
        services.TryAddSingleton<IPostgresLifecycleClient, PostgresLifecycleClient>();
        services.TryAddSingleton<IDbWaker, DbWaker>();
        services.TryAddSingleton<ActivityCommandInterceptor>();
        services.AddHostedService<StartupWakeHostedService>();
        services.AddHostedService<AutoStopHostedService>();
        services.AddHostedService<ShutdownStopHostedService>();

        return services;
    }

    public static IServiceCollection WakeOnApplicationStartup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Configure<AzurePostgresAutoSleepOptions>(o => o.WakeOnStartup = true);
        return services;
    }

    public static IHealthChecksBuilder AddAzurePostgresAutoSleepHealthCheck(
        this IHealthChecksBuilder builder,
        string name = "postgres-autosleep",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCheck<PostgresAutoSleepHealthCheck>(
            name,
            failureStatus,
            tags ?? Array.Empty<string>());
    }
}
