using AzurePostgresFlexibleAutoSleep.Activity;
using AzurePostgresFlexibleAutoSleep.Lifecycle;
using AzurePostgresFlexibleAutoSleep.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AzurePostgresFlexibleAutoSleep.Tests;

public class AutoStopHostedServiceTests
{
    private static AutoStopHostedService Create(
        FakePostgresLifecycleClient lifecycle,
        DbActivityTracker tracker,
        FakeTimeProvider clock,
        TimeSpan? idleThreshold = null)
    {
        var options = Options.Create(new AzurePostgresAutoSleepOptions
        {
            ResourceId = "/subscriptions/x/resourceGroups/y/providers/Microsoft.DBforPostgreSQL/flexibleServers/z",
            IdleThreshold = idleThreshold ?? TimeSpan.FromMinutes(15),
        });
        return new AutoStopHostedService(options, tracker, lifecycle, NullLogger<AutoStopHostedService>.Instance, clock);
    }

    [Fact]
    public async Task Stops_when_idle_threshold_exceeded_and_state_ready()
    {
        var clock = new FakeTimeProvider();
        var tracker = new DbActivityTracker(clock);
        var lifecycle = new FakePostgresLifecycleClient { State = PostgresServerState.Ready };
        var svc = Create(lifecycle, tracker, clock, TimeSpan.FromMinutes(15));

        clock.Advance(TimeSpan.FromMinutes(16));
        await svc.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal(1, lifecycle.StopCalls);
    }

    [Fact]
    public async Task Does_not_stop_when_activity_is_recent()
    {
        var clock = new FakeTimeProvider();
        var tracker = new DbActivityTracker(clock);
        var lifecycle = new FakePostgresLifecycleClient { State = PostgresServerState.Ready };
        var svc = Create(lifecycle, tracker, clock, TimeSpan.FromMinutes(15));

        clock.Advance(TimeSpan.FromMinutes(5));
        await svc.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal(0, lifecycle.StopCalls);
    }

    [Fact]
    public async Task Does_not_stop_when_already_stopped()
    {
        var clock = new FakeTimeProvider();
        var tracker = new DbActivityTracker(clock);
        var lifecycle = new FakePostgresLifecycleClient { State = PostgresServerState.Stopped };
        var svc = Create(lifecycle, tracker, clock, TimeSpan.FromMinutes(15));

        clock.Advance(TimeSpan.FromMinutes(20));
        await svc.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal(0, lifecycle.StopCalls);
    }

    [Fact]
    public async Task Swallows_transient_lifecycle_exceptions()
    {
        var clock = new FakeTimeProvider();
        var tracker = new DbActivityTracker(clock);
        var lifecycle = new FakePostgresLifecycleClient
        {
            State = PostgresServerState.Ready,
            StopException = new InvalidOperationException("transient"),
        };
        var svc = Create(lifecycle, tracker, clock, TimeSpan.FromMinutes(15));

        clock.Advance(TimeSpan.FromMinutes(16));
        await svc.EvaluateOnceAsync(CancellationToken.None);

        Assert.Equal(1, lifecycle.StopCalls);
    }
}
