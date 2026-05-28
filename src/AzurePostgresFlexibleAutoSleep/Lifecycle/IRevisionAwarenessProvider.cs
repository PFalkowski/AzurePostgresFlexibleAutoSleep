namespace AzurePostgresFlexibleAutoSleep.Lifecycle;

/// <summary>
/// Optional extension point consulted by the <c>StopOnShutdown</c> handler before stopping the server.
/// Lets a platform-specific implementation distinguish a scale-in (stop is wanted) from a rolling
/// redeploy (a new revision is coming up, so the DB should stay awake).
/// </summary>
/// <remarks>
/// No implementation ships in this package. When none is registered, the shutdown decision rests on the
/// idle gate alone. Register one (e.g. an Azure Container Apps revision-list check) to add deploy detection
/// without an API break.
/// </remarks>
public interface IRevisionAwarenessProvider
{
    /// <summary>
    /// Returns true when another revision/deployment of this app is starting up, in which case the
    /// shutdown handler must not stop the server.
    /// </summary>
    Task<bool> IsDeployInProgressAsync(CancellationToken ct = default);
}
