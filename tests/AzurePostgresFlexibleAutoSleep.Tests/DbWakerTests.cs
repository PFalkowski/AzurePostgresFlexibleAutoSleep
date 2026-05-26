using AzurePostgresFlexibleAutoSleep.Activity;
using AzurePostgresFlexibleAutoSleep.Tests.Fakes;
using Xunit;

namespace AzurePostgresFlexibleAutoSleep.Tests;

public class DbWakerTests
{
    [Fact]
    public async Task EnsureAwakeAsync_delegates_then_records_activity()
    {
        var lifecycle = new FakePostgresLifecycleClient();
        var tracker = new DbActivityTracker();
        var before = tracker.LastActivity;
        var waker = new DbWaker(lifecycle, tracker);

        await Task.Delay(2);
        await waker.EnsureAwakeAsync();

        Assert.Equal(1, lifecycle.EnsureAwakeCalls);
        Assert.True(tracker.LastActivity > before);
    }
}
