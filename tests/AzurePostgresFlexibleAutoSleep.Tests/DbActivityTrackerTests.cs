using AzurePostgresFlexibleAutoSleep.Activity;
using Xunit;

namespace AzurePostgresFlexibleAutoSleep.Tests;

public class DbActivityTrackerTests
{
    [Fact]
    public void RecordActivity_advances_LastActivity()
    {
        var tracker = new DbActivityTracker();
        var before = tracker.LastActivity;

        Thread.Sleep(2);
        tracker.RecordActivity();

        Assert.True(tracker.LastActivity > before);
    }

    [Fact]
    public async Task LastActivity_never_decreases_under_concurrent_writers()
    {
        var tracker = new DbActivityTracker();
        const int threads = 16;
        const int iterations = 5_000;
        using var start = new ManualResetEventSlim();

        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < iterations; i++)
            {
                tracker.RecordActivity();
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(tasks);

        var afterAll = DateTimeOffset.UtcNow;
        Assert.True(tracker.LastActivity <= afterAll);
        Assert.True(tracker.LastActivity > DateTimeOffset.UtcNow.AddSeconds(-10));
    }

    [Fact]
    public void LastActivity_returns_latest_when_written_with_explicit_timestamps()
    {
        var tracker = new DbActivityTracker();
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-5);
        var t2 = DateTimeOffset.UtcNow;

        tracker.RecordActivityAt(t2);
        tracker.RecordActivityAt(t1);

        Assert.Equal(t2, tracker.LastActivity);
    }
}
