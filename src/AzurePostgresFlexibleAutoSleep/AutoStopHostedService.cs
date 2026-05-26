using AzurePostgresFlexibleAutoSleep.Activity;
using AzurePostgresFlexibleAutoSleep.Lifecycle;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzurePostgresFlexibleAutoSleep;

public sealed class AutoStopHostedService : BackgroundService
{
    private readonly AzurePostgresAutoSleepOptions _options;
    private readonly IDbActivityTracker _activity;
    private readonly IPostgresLifecycleClient _lifecycle;
    private readonly ILogger<AutoStopHostedService> _logger;
    private readonly TimeProvider _clock;

    public AutoStopHostedService(
        IOptions<AzurePostgresAutoSleepOptions> options,
        IDbActivityTracker activity,
        IPostgresLifecycleClient lifecycle,
        ILogger<AutoStopHostedService> logger,
        TimeProvider? clock = null)
    {
        _options = options.Value;
        _activity = activity;
        _lifecycle = lifecycle;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("AzurePostgresAutoSleep disabled; auto-stop loop will not run.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.StopCheckInterval, _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await EvaluateOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task EvaluateOnceAsync(CancellationToken ct)
    {
        try
        {
            var idleFor = _clock.GetUtcNow() - _activity.LastActivity;
            if (idleFor < _options.IdleThreshold)
            {
                return;
            }

            var state = await _lifecycle.GetStateAsync(ct).ConfigureAwait(false);
            if (state != PostgresServerState.Ready)
            {
                return;
            }

            await _lifecycle.StopAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Stopped Postgres flexible server after {IdleSeconds:F0}s idle.",
                idleFor.TotalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AutoStop tick failed; will retry on next interval.");
        }
    }
}
