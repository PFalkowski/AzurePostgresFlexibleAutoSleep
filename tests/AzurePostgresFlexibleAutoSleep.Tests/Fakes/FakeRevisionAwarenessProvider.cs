using AzurePostgresFlexibleAutoSleep.Lifecycle;

namespace AzurePostgresFlexibleAutoSleep.Tests.Fakes;

public sealed class FakeRevisionAwarenessProvider : IRevisionAwarenessProvider
{
    public bool DeployInProgress { get; set; }
    public int Calls;

    public Task<bool> IsDeployInProgressAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(DeployInProgress);
    }
}
