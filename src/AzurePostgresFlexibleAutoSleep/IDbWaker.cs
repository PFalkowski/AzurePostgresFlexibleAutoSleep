namespace AzurePostgresFlexibleAutoSleep;

public interface IDbWaker
{
    Task EnsureAwakeAsync(CancellationToken ct = default);
}
