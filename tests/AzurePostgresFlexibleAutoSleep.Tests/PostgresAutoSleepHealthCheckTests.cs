using AzurePostgresFlexibleAutoSleep.Lifecycle;
using AzurePostgresFlexibleAutoSleep.Tests.Fakes;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace AzurePostgresFlexibleAutoSleep.Tests;

public class PostgresAutoSleepHealthCheckTests
{
    private static async Task<HealthStatus> StatusFor(PostgresServerState state)
    {
        var check = new PostgresAutoSleepHealthCheck(new FakePostgresLifecycleClient { State = state });
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        return result.Status;
    }

    [Theory]
    [InlineData(PostgresServerState.Ready, HealthStatus.Healthy)]
    [InlineData(PostgresServerState.Stopped, HealthStatus.Healthy)]
    [InlineData(PostgresServerState.Starting, HealthStatus.Degraded)]
    [InlineData(PostgresServerState.Stopping, HealthStatus.Degraded)]
    [InlineData(PostgresServerState.Dropping, HealthStatus.Unhealthy)]
    [InlineData(PostgresServerState.Failed, HealthStatus.Unhealthy)]
    [InlineData(PostgresServerState.Unknown, HealthStatus.Unhealthy)]
    public async Task Maps_server_state_to_health_status(PostgresServerState state, HealthStatus expected)
    {
        Assert.Equal(expected, await StatusFor(state));
    }

    [Fact]
    public async Task Surfaces_lifecycle_exception_as_unhealthy()
    {
        var lifecycle = new ThrowingLifecycleClient(new InvalidOperationException("boom"));
        var check = new PostgresAutoSleepHealthCheck(lifecycle);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.IsType<InvalidOperationException>(result.Exception);
    }

    private sealed class ThrowingLifecycleClient : IPostgresLifecycleClient
    {
        private readonly Exception _ex;
        public ThrowingLifecycleClient(Exception ex) => _ex = ex;
        public Task<PostgresServerState> GetStateAsync(CancellationToken ct = default) => throw _ex;
        public Task EnsureAwakeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
