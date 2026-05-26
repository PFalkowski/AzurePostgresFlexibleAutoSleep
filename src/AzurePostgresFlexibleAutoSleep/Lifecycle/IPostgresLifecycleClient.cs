namespace AzurePostgresFlexibleAutoSleep.Lifecycle;

public interface IPostgresLifecycleClient
{
    Task<PostgresServerState> GetStateAsync(CancellationToken ct = default);

    Task EnsureAwakeAsync(CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);
}
