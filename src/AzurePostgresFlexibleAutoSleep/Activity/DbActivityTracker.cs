namespace AzurePostgresFlexibleAutoSleep.Activity;

public sealed class DbActivityTracker : IDbActivityTracker
{
    private long _lastActivityUtcTicks = DateTime.UtcNow.Ticks;

    public DateTimeOffset LastActivity =>
        new(Interlocked.Read(ref _lastActivityUtcTicks), TimeSpan.Zero);

    public void RecordActivity() => RecordActivityAt(DateTimeOffset.UtcNow);

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
