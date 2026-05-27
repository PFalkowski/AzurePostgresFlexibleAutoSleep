using AzurePostgresFlexibleAutoSleep.Lifecycle;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AzurePostgresFlexibleAutoSleep;

public sealed class PostgresAutoSleepHealthCheck : IHealthCheck
{
    private readonly IPostgresLifecycleClient _lifecycle;

    public PostgresAutoSleepHealthCheck(IPostgresLifecycleClient lifecycle)
    {
        _lifecycle = lifecycle;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        PostgresServerState state;
        try
        {
            state = await _lifecycle.GetStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                description: "Failed to query Postgres flexible server state.",
                exception: ex);
        }

        var data = new Dictionary<string, object> { ["state"] = state.ToString() };

        return state switch
        {
            PostgresServerState.Ready
                => HealthCheckResult.Healthy("Postgres flexible server is Ready.", data),
            PostgresServerState.Stopped
                => HealthCheckResult.Healthy("Postgres flexible server is Stopped (no traffic; will wake on demand).", data),
            PostgresServerState.Starting or PostgresServerState.Stopping
                => HealthCheckResult.Degraded($"Postgres flexible server is {state} (transient).", data: data),
            _ => HealthCheckResult.Unhealthy(
                $"Postgres flexible server is in non-serviceable state {state}.",
                data: data),
        };
    }
}
