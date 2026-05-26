using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.PostgreSql.FlexibleServers;
using Azure.ResourceManager.PostgreSql.FlexibleServers.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzurePostgresFlexibleAutoSleep.Lifecycle;

public sealed class PostgresLifecycleClient : IPostgresLifecycleClient
{
    private readonly AzurePostgresAutoSleepOptions _options;
    private readonly ILogger<PostgresLifecycleClient> _logger;
    private readonly TimeProvider _clock;
    private readonly StateCache _cache;
    private readonly SemaphoreSlim _transitionLock = new(1, 1);
    private readonly PostgreSqlFlexibleServerResource _server;

    public PostgresLifecycleClient(
        IOptions<AzurePostgresAutoSleepOptions> options,
        ILogger<PostgresLifecycleClient> logger,
        TimeProvider? clock = null)
    {
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _cache = new StateCache(_options.StateCacheLifetime, _clock);

        var credential = _options.Credential ?? new DefaultAzureCredential();
        var arm = new ArmClient(credential);
        var id = new ResourceIdentifier(_options.ResourceId);
        _server = arm.GetPostgreSqlFlexibleServerResource(id);
    }

    public Task<PostgresServerState> GetStateAsync(CancellationToken ct = default) =>
        _cache.GetAsync(FetchStateFromArmAsync, ct);

    public async Task EnsureAwakeAsync(CancellationToken ct = default)
    {
        var state = await GetStateAsync(ct).ConfigureAwait(false);
        if (state == PostgresServerState.Ready)
        {
            return;
        }

        await _transitionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            state = await GetStateAsync(ct).ConfigureAwait(false);
            if (state == PostgresServerState.Ready)
            {
                return;
            }

            if (state is PostgresServerState.Stopped or PostgresServerState.Failed or PostgresServerState.Unknown)
            {
                _logger.LogInformation("Starting Postgres flexible server {ResourceId}", _options.ResourceId);
                await _server.StartAsync(Azure.WaitUntil.Started, ct).ConfigureAwait(false);
                _cache.Set(PostgresServerState.Starting);
            }
            else if (state == PostgresServerState.Stopping)
            {
                await WaitForAsync(s => s == PostgresServerState.Stopped, ct).ConfigureAwait(false);
                _logger.LogInformation("Starting Postgres flexible server {ResourceId}", _options.ResourceId);
                await _server.StartAsync(Azure.WaitUntil.Started, ct).ConfigureAwait(false);
                _cache.Set(PostgresServerState.Starting);
            }

            await WaitForAsync(s => s == PostgresServerState.Ready, ct).ConfigureAwait(false);
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _transitionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await GetStateAsync(ct).ConfigureAwait(false);
            if (state is PostgresServerState.Stopped or PostgresServerState.Stopping)
            {
                return;
            }

            _logger.LogInformation("Stopping Postgres flexible server {ResourceId}", _options.ResourceId);
            await _server.StopAsync(Azure.WaitUntil.Started, ct).ConfigureAwait(false);
            _cache.Set(PostgresServerState.Stopping);
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    private async Task WaitForAsync(Func<PostgresServerState, bool> predicate, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(_options.WakeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        while (!linked.IsCancellationRequested)
        {
            _cache.Invalidate();
            var state = await _cache.GetAsync(FetchStateFromArmAsync, linked.Token).ConfigureAwait(false);
            if (predicate(state))
            {
                return;
            }
            await Task.Delay(_options.WakePollInterval, _clock, linked.Token).ConfigureAwait(false);
        }

        if (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Postgres flexible server '{_options.ResourceId}' did not reach the expected state within {_options.WakeTimeout}.");
        }

        ct.ThrowIfCancellationRequested();
    }

    private async Task<PostgresServerState> FetchStateFromArmAsync(CancellationToken ct)
    {
        var response = await _server.GetAsync(ct).ConfigureAwait(false);
        var armState = response.Value.Data.State;
        return MapState(armState);
    }

    internal static PostgresServerState MapState(PostgreSqlFlexibleServerState? armState) => armState?.ToString() switch
    {
        "Ready" => PostgresServerState.Ready,
        "Starting" => PostgresServerState.Starting,
        "Stopping" => PostgresServerState.Stopping,
        "Stopped" => PostgresServerState.Stopped,
        "Disabled" => PostgresServerState.Stopped,
        "Updating" => PostgresServerState.Ready,
        "Dropping" => PostgresServerState.Failed,
        _ => PostgresServerState.Unknown,
    };
}
