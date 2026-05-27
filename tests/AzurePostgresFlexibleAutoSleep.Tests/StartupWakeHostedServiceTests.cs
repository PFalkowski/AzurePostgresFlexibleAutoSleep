using AzurePostgresFlexibleAutoSleep.Activity;
using AzurePostgresFlexibleAutoSleep.Lifecycle;
using AzurePostgresFlexibleAutoSleep.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzurePostgresFlexibleAutoSleep.Tests;

public class StartupWakeHostedServiceTests
{
    private static AzurePostgresAutoSleepOptions DefaultOptions(Action<AzurePostgresAutoSleepOptions>? mutate = null)
    {
        var opts = new AzurePostgresAutoSleepOptions
        {
            ResourceId = "/subscriptions/x/resourceGroups/y/providers/Microsoft.DBforPostgreSQL/flexibleServers/z",
            WakeOnStartup = true,
            StartupWakeTimeout = TimeSpan.FromSeconds(5),
        };
        mutate?.Invoke(opts);
        return opts;
    }

    [Fact]
    public async Task StartAsync_wakes_db_and_records_activity_when_enabled()
    {
        var lifecycle = new FakePostgresLifecycleClient { State = PostgresServerState.Stopped };
        var tracker = new DbActivityTracker();
        var waker = new DbWaker(lifecycle, tracker);
        var before = tracker.LastActivity;
        await Task.Delay(2);

        var svc = new StartupWakeHostedService(
            waker,
            Options.Create(DefaultOptions()),
            NullLogger<StartupWakeHostedService>.Instance);

        await svc.StartAsync(CancellationToken.None);

        Assert.Equal(1, lifecycle.EnsureAwakeCalls);
        Assert.True(tracker.LastActivity > before);
    }

    [Fact]
    public async Task StartAsync_noops_when_WakeOnStartup_false()
    {
        var lifecycle = new FakePostgresLifecycleClient();
        var waker = new DbWaker(lifecycle, new DbActivityTracker());

        var svc = new StartupWakeHostedService(
            waker,
            Options.Create(DefaultOptions(o => o.WakeOnStartup = false)),
            NullLogger<StartupWakeHostedService>.Instance);

        await svc.StartAsync(CancellationToken.None);

        Assert.Equal(0, lifecycle.EnsureAwakeCalls);
    }

    [Fact]
    public async Task StartAsync_noops_when_Enabled_false()
    {
        var lifecycle = new FakePostgresLifecycleClient();
        var waker = new DbWaker(lifecycle, new DbActivityTracker());

        var svc = new StartupWakeHostedService(
            waker,
            Options.Create(DefaultOptions(o => o.Enabled = false)),
            NullLogger<StartupWakeHostedService>.Instance);

        await svc.StartAsync(CancellationToken.None);

        Assert.Equal(0, lifecycle.EnsureAwakeCalls);
    }

    [Fact]
    public async Task StartAsync_throws_on_wake_failure_to_fail_fast()
    {
        var lifecycle = new FakePostgresLifecycleClient
        {
            State = PostgresServerState.Stopped,
            EnsureAwakeException = new InvalidOperationException("boom"),
        };
        var waker = new DbWaker(lifecycle, new DbActivityTracker());

        var svc = new StartupWakeHostedService(
            waker,
            Options.Create(DefaultOptions()),
            NullLogger<StartupWakeHostedService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_times_out_per_StartupWakeTimeout()
    {
        var lifecycle = new FakePostgresLifecycleClient
        {
            State = PostgresServerState.Stopped,
            EnsureAwakeDelay = TimeSpan.FromSeconds(5),
        };
        var waker = new DbWaker(lifecycle, new DbActivityTracker());

        var svc = new StartupWakeHostedService(
            waker,
            Options.Create(DefaultOptions(o => o.StartupWakeTimeout = TimeSpan.FromMilliseconds(50))),
            NullLogger<StartupWakeHostedService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.StartAsync(CancellationToken.None));
    }

    [Fact]
    public Task StopAsync_is_noop()
    {
        var lifecycle = new FakePostgresLifecycleClient();
        var waker = new DbWaker(lifecycle, new DbActivityTracker());

        var svc = new StartupWakeHostedService(
            waker,
            Options.Create(DefaultOptions()),
            NullLogger<StartupWakeHostedService>.Instance);

        return svc.StopAsync(CancellationToken.None);
    }
}
