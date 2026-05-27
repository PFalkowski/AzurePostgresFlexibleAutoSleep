using Azure.Core;

namespace AzurePostgresFlexibleAutoSleep;

public sealed class AzurePostgresAutoSleepOptions
{
    public bool Enabled { get; set; } = true;

    public required string ResourceId { get; set; }

    public TimeSpan IdleThreshold { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan WakeTimeout { get; set; } = TimeSpan.FromSeconds(120);

    public TimeSpan WakePollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan StopCheckInterval { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan StateCacheLifetime { get; set; } = TimeSpan.FromSeconds(30);

    public List<string> ExemptPaths { get; set; } = new() { "/healthz" };

    public bool WakeOnStartup { get; set; } = false;

    public TimeSpan StartupWakeTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TokenCredential? Credential { get; set; }
}
