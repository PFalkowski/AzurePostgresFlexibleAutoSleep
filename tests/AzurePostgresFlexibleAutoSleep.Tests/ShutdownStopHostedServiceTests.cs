using AzurePostgresFlexibleAutoSleep.Activity;
using AzurePostgresFlexibleAutoSleep.Lifecycle;
using AzurePostgresFlexibleAutoSleep.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AzurePostgresFlexibleAutoSleep.Tests;

public class ShutdownStopHostedServiceTests
{
    private static ShutdownStopHostedService Create(
        FakePostgresLifecycleClient lifecycle,
        DbActivityTracker tracker,
        FakeTimeProvider clock,
        FakeHostApplicationLifetime lifetime,
        FakeRevisionAwarenessProvider? revisions = null,
        bool stopOnShutdown = true)
    {
        var options = Options.Create(new AzurePostgresAutoSleepOptions
        {
            ResourceId = "/subscriptions/x/resourceGroups/y/providers/Microsoft.DBforPostgreSQL/flexibleServers/z",
            IdleThreshold = TimeSpan.FromMinutes(15),
            StopOnShutdown = stopOnShutdown,
        });
        return new ShutdownStopHostedService(
            options, tracker, lifecycle, lifetime,
            NullLogger<ShutdownStopHostedService>.Instance, clock, revisions);
    }

    [Fact]
    public async Task Does_not_stop_when_activity_is_recent()
    {
        var clock = new FakeTimeProvider();
        var tracker = new DbActivityTracker(clock);
        var lifecycle = new FakePostgresLifecycleClient { State = PostgresServerState.Ready };
        using var lifetime = new FakeHostApplicationLifetime();
        var svc = Create(lifecycle, tracker, clock, lifetime);

        clock.Advance(TimeSpan.FromMinutes(5));
        await svc.EvaluateShutdownAsync(CancellationToken.None);

        Assert.Equal(0, lifecycle.StopCalls);
    }

    [Fact]
    public async Task Stops_when_idle_and_no_revision_provider()
    {
        var clock = new FakeTimeProvider();
        var tracker = new DbActivityTracker(clock);
        var lifecycle = new FakePostgresLifecycleClient { State = PostgresServerState.Ready };
        using var lifetime = new FakeHostApplicationLifetime();
        var svc = Create(lifecycle, tracker, clock, lifetime);

        clock.Advance(TimeSpan.FromMinutes(16));
        await svc.EvaluateShutdownAsync(CancellationToken.None);

        Assert.Equal(1, lifecycle.StopCalls);
    }

    [Fact]
    public async Task Does_not_stop_when_revision_provider_reports_deploy()
    {
        var clock = new FakeTimeProvider();
        var tracker = new DbActivityTracker(clock);
        var lifecycle = new FakePostgresLifecycleClient { State = PostgresServerState.Ready };
        using var lifetime = new FakeHostApplicationLifetime();
        var revisions = new FakeRevisionAwarenessProvider { DeployInProgress = true };
        var svc = Create(lifecycle, tracker, clock, lifetime, revisions);

        clock.Advance(TimeSpan.FromMinutes(16));
        await svc.EvaluateShutdownAsync(CancellationToken.None);

        Assert.Equal(1, revisions.Calls);
        Assert.Equal(0, lifecycle.StopCalls);
    }

    [Fact]
    public async Task Stops_when_revision_provider_reports_no_deploy()
    {
        var clock = new FakeTimeProvider();
        var tracker = new DbActivityTracker(clock);
        var lifecycle = new FakePostgresLifecycleClient { State = PostgresServerState.Ready };
        using var lifetime = new FakeHostApplicationLifetime();
        var revisions = new FakeRevisionAwarenessProvider { DeployInProgress = false };
        var svc = Create(lifecycle, tracker, clock, lifetime, revisions);

        clock.Advance(TimeSpan.FromMinutes(16));
        await svc.EvaluateShutdownAsync(CancellationToken.None);

        Assert.Equal(1, lifecycle.StopCalls);
    }

    [Fact]
    public async Task Swallows_stop_exceptions_so_process_can_exit()
    {
        var clock = new FakeTimeProvider();
        var tracker = new DbActivityTracker(clock);
        var lifecycle = new FakePostgresLifecycleClient
        {
            State = PostgresServerState.Ready,
            StopException = new InvalidOperationException("transient"),
        };
        using var lifetime = new FakeHostApplicationLifetime();
        var svc = Create(lifecycle, tracker, clock, lifetime);

        clock.Advance(TimeSpan.FromMinutes(16));
        await svc.EvaluateShutdownAsync(CancellationToken.None);

        Assert.Equal(1, lifecycle.StopCalls);
    }

    [Fact]
    public async Task ApplicationStopping_triggers_stop_when_enabled_and_idle()
    {
        var clock = new FakeTimeProvider();
        var tracker = new DbActivityTracker(clock);
        var lifecycle = new FakePostgresLifecycleClient { State = PostgresServerState.Ready };
        using var lifetime = new FakeHostApplicationLifetime();
        var svc = Create(lifecycle, tracker, clock, lifetime);

        await svc.StartAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(16));
        lifetime.StopApplication();

        Assert.Equal(1, lifecycle.StopCalls);
    }

    [Fact]
    public async Task ApplicationStopping_does_nothing_when_disabled()
    {
        var clock = new FakeTimeProvider();
        var tracker = new DbActivityTracker(clock);
        var lifecycle = new FakePostgresLifecycleClient { State = PostgresServerState.Ready };
        using var lifetime = new FakeHostApplicationLifetime();
        var svc = Create(lifecycle, tracker, clock, lifetime, stopOnShutdown: false);

        await svc.StartAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(16));
        lifetime.StopApplication();

        Assert.Equal(0, lifecycle.StopCalls);
    }
}
