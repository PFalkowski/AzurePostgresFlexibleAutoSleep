using AzurePostgresFlexibleAutoSleep.Activity;
using AzurePostgresFlexibleAutoSleep.Lifecycle;

namespace AzurePostgresFlexibleAutoSleep;

public sealed class DbWaker : IDbWaker
{
    private readonly IPostgresLifecycleClient _lifecycle;
    private readonly IDbActivityTracker _activity;

    public DbWaker(IPostgresLifecycleClient lifecycle, IDbActivityTracker activity)
    {
        _lifecycle = lifecycle;
        _activity = activity;
    }

    public async Task EnsureAwakeAsync(CancellationToken ct = default)
    {
        await _lifecycle.EnsureAwakeAsync(ct).ConfigureAwait(false);
        _activity.RecordActivity();
    }
}
