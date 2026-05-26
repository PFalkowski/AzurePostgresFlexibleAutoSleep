using Xunit;

namespace AzurePostgresFlexibleAutoSleep.Tests;

public class AzurePostgresAutoSleepOptionsTests
{
    [Fact]
    public void Defaults_match_documented_values()
    {
        var opts = new AzurePostgresAutoSleepOptions { ResourceId = "rid" };

        Assert.True(opts.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(15), opts.IdleThreshold);
        Assert.Equal(TimeSpan.FromSeconds(120), opts.WakeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), opts.WakePollInterval);
        Assert.Equal(TimeSpan.FromMinutes(1), opts.StopCheckInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), opts.StateCacheLifetime);
        Assert.Equal(new[] { "/healthz" }, opts.ExemptPaths);
        Assert.Null(opts.Credential);
    }
}
