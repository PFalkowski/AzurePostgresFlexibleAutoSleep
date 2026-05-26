using Azure.ResourceManager.PostgreSql.FlexibleServers.Models;
using AzurePostgresFlexibleAutoSleep.Lifecycle;
using Xunit;

namespace AzurePostgresFlexibleAutoSleep.Tests;

public class PostgresLifecycleClientTests
{
    [Theory]
    [InlineData("Ready", PostgresServerState.Ready)]
    [InlineData("Starting", PostgresServerState.Starting)]
    [InlineData("Stopping", PostgresServerState.Stopping)]
    [InlineData("Stopped", PostgresServerState.Stopped)]
    [InlineData("Disabled", PostgresServerState.Stopped)]
    [InlineData("Updating", PostgresServerState.Starting)]
    [InlineData("Dropping", PostgresServerState.Dropping)]
    [InlineData("WhoKnows", PostgresServerState.Unknown)]
    public void MapState_translates_arm_states(string armState, PostgresServerState expected)
    {
        var mapped = PostgresLifecycleClient.MapState(new PostgreSqlFlexibleServerState(armState));
        Assert.Equal(expected, mapped);
    }

    [Fact]
    public void MapState_null_is_unknown()
    {
        Assert.Equal(PostgresServerState.Unknown, PostgresLifecycleClient.MapState(null));
    }
}
