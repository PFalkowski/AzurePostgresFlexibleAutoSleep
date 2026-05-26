namespace AzurePostgresFlexibleAutoSleep.Lifecycle;

public sealed class StateCache
{
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private int _state = (int)PostgresServerState.Unknown;
    private long _fetchedAtTicks = DateTimeOffset.MinValue.UtcTicks;

    public StateCache(TimeSpan ttl, TimeProvider? clock = null)
    {
        _ttl = ttl;
        _clock = clock ?? TimeProvider.System;
    }

    public PostgresServerState PeekCachedOrUnknown() =>
        (PostgresServerState)Volatile.Read(ref _state);

    public void Set(PostgresServerState state)
    {
        Volatile.Write(ref _state, (int)state);
        Interlocked.Exchange(ref _fetchedAtTicks, _clock.GetUtcNow().UtcTicks);
    }

    public void Invalidate()
    {
        Volatile.Write(ref _state, (int)PostgresServerState.Unknown);
        Interlocked.Exchange(ref _fetchedAtTicks, DateTimeOffset.MinValue.UtcTicks);
    }

    public async Task<PostgresServerState> GetAsync(
        Func<CancellationToken, Task<PostgresServerState>> refresh,
        CancellationToken ct)
    {
        if (IsFresh())
        {
            return (PostgresServerState)Volatile.Read(ref _state);
        }

        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsFresh())
            {
                return (PostgresServerState)Volatile.Read(ref _state);
            }

            var fresh = await refresh(ct).ConfigureAwait(false);
            Set(fresh);
            return fresh;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool IsFresh()
    {
        var current = (PostgresServerState)Volatile.Read(ref _state);
        if (current == PostgresServerState.Unknown)
        {
            return false;
        }
        var fetchedAt = new DateTimeOffset(Interlocked.Read(ref _fetchedAtTicks), TimeSpan.Zero);
        return _clock.GetUtcNow() - fetchedAt < _ttl;
    }
}
