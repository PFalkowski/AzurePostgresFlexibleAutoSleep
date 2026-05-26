using AzurePostgresFlexibleAutoSleep.Lifecycle;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AzurePostgresFlexibleAutoSleep.Tests;

public class StateCacheTests
{
    [Fact]
    public async Task Within_ttl_returns_cached_value_without_calling_refresh()
    {
        var clock = new FakeTimeProvider();
        var cache = new StateCache(TimeSpan.FromSeconds(30), clock);
        var calls = 0;

        Task<PostgresServerState> Refresh(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(PostgresServerState.Ready);
        }

        var first = await cache.GetAsync(Refresh, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(10));
        var second = await cache.GetAsync(Refresh, CancellationToken.None);

        Assert.Equal(PostgresServerState.Ready, first);
        Assert.Equal(PostgresServerState.Ready, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task After_ttl_expiry_refresh_is_called_again()
    {
        var clock = new FakeTimeProvider();
        var cache = new StateCache(TimeSpan.FromSeconds(30), clock);
        var calls = 0;

        Task<PostgresServerState> Refresh(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(PostgresServerState.Ready);
        }

        await cache.GetAsync(Refresh, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(31));
        await cache.GetAsync(Refresh, CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Concurrent_callers_during_refresh_in_flight_do_not_stampede()
    {
        var clock = new FakeTimeProvider();
        var cache = new StateCache(TimeSpan.FromSeconds(30), clock);
        var calls = 0;
        var gate = new TaskCompletionSource();

        async Task<PostgresServerState> Refresh(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return PostgresServerState.Ready;
        }

        var t1 = cache.GetAsync(Refresh, CancellationToken.None);
        var t2 = cache.GetAsync(Refresh, CancellationToken.None);
        var t3 = cache.GetAsync(Refresh, CancellationToken.None);

        gate.SetResult();
        await Task.WhenAll(t1, t2, t3);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Set_updates_cached_value_and_timestamp()
    {
        var clock = new FakeTimeProvider();
        var cache = new StateCache(TimeSpan.FromSeconds(30), clock);

        cache.Set(PostgresServerState.Stopped);

        Assert.Equal(PostgresServerState.Stopped, cache.PeekCachedOrUnknown());
    }

    [Fact]
    public void Invalidate_forces_next_get_to_refresh()
    {
        var clock = new FakeTimeProvider();
        var cache = new StateCache(TimeSpan.FromSeconds(30), clock);
        cache.Set(PostgresServerState.Ready);

        cache.Invalidate();

        Assert.Equal(PostgresServerState.Unknown, cache.PeekCachedOrUnknown());
    }
}
