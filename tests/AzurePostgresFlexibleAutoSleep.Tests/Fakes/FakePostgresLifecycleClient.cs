using AzurePostgresFlexibleAutoSleep.Lifecycle;

namespace AzurePostgresFlexibleAutoSleep.Tests.Fakes;

public sealed class FakePostgresLifecycleClient : IPostgresLifecycleClient
{
    public PostgresServerState State { get; set; } = PostgresServerState.Ready;
    public int EnsureAwakeCalls;
    public int StopCalls;
    public int GetStateCalls;

    public TimeSpan EnsureAwakeDelay { get; set; } = TimeSpan.Zero;
    public Exception? EnsureAwakeException { get; set; }
    public Exception? StopException { get; set; }

    public async Task<PostgresServerState> GetStateAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref GetStateCalls);
        await Task.Yield();
        return State;
    }

    public async Task EnsureAwakeAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref EnsureAwakeCalls);
        if (EnsureAwakeDelay > TimeSpan.Zero)
        {
            await Task.Delay(EnsureAwakeDelay, ct).ConfigureAwait(false);
        }
        if (EnsureAwakeException is not null)
        {
            throw EnsureAwakeException;
        }
        State = PostgresServerState.Ready;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref StopCalls);
        if (StopException is not null)
        {
            throw StopException;
        }
        State = PostgresServerState.Stopped;
        return Task.CompletedTask;
    }
}
