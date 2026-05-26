namespace AzurePostgresFlexibleAutoSleep.Activity;

public interface IDbActivityTracker
{
    DateTimeOffset LastActivity { get; }
    void RecordActivity();
}
