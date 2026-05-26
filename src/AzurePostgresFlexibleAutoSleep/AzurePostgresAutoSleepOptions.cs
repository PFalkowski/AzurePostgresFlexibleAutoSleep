using Azure.Core;

namespace AzurePostgresFlexibleAutoSleep;

public sealed class AzurePostgresAutoSleepOptions
{
    public bool Enabled { get; set; } = true;

    public required string ResourceId { get; init; }

    public TimeSpan IdleThreshold { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan WakeTimeout { get; set; } = TimeSpan.FromSeconds(120);

    public TimeSpan WakePollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan StopCheckInterval { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan StateCacheLifetime { get; set; } = TimeSpan.FromSeconds(30);

    public List<string> ExemptPaths { get; set; } = new() { "/healthz" };

    public TokenCredential? Credential { get; set; }
}
