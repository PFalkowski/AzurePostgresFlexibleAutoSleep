namespace AzurePostgresFlexibleAutoSleep.Lifecycle;

public enum PostgresServerState
{
    Unknown = 0,
    Stopped,
    Starting,
    Ready,
    Stopping,
    Failed,
}
