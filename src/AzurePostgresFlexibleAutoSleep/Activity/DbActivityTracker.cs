namespace AzurePostgresFlexibleAutoSleep.Activity;

public sealed class DbActivityTracker : IDbActivityTracker
{
    private readonly TimeProvider _clock;
    private long _lastActivityUtcTicks;

    public DbActivityTracker(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _lastActivityUtcTicks = _clock.GetUtcNow().UtcTicks;
    }

    public DateTimeOffset LastActivity =>
        new(Interlocked.Read(ref _lastActivityUtcTicks), TimeSpan.Zero);

    public void RecordActivity() => RecordActivityAt(_clock.GetUtcNow());

    internal void RecordActivityAt(DateTimeOffset timestamp)
    {
        var incoming = timestamp.UtcTicks;
        long current;
        do
        {
            current = Interlocked.Read(ref _lastActivityUtcTicks);
            if (incoming <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _lastActivityUtcTicks, incoming, current) != current);
    }
}
