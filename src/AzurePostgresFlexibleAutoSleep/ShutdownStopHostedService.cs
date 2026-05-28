using AzurePostgresFlexibleAutoSleep.Activity;
using AzurePostgresFlexibleAutoSleep.Lifecycle;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzurePostgresFlexibleAutoSleep;

/// <summary>
/// Stops an idle server on graceful host shutdown so the DB can sleep on hosts that scale to zero,
/// where <see cref="AutoStopHostedService"/>'s polling loop dies with the last replica.
/// </summary>
/// <remarks>
/// The work is registered against <see cref="IHostApplicationLifetime.ApplicationStopping"/> rather than
/// <see cref="IHostedService.StopAsync"/>: a <see cref="BackgroundService"/>'s StopAsync runs before
/// lifetime handlers, by which point the lifecycle client's dependencies may be tearing down. Everything
/// the handler needs is captured at registration.
/// </remarks>
public sealed class ShutdownStopHostedService : IHostedService, IDisposable
{
    private readonly AzurePostgresAutoSleepOptions _options;
    private readonly IDbActivityTracker _activity;
    private readonly IPostgresLifecycleClient _lifecycle;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ShutdownStopHostedService> _logger;
    private readonly TimeProvider _clock;
    private readonly IRevisionAwarenessProvider? _revisions;
    private CancellationTokenRegistration _registration;

    public ShutdownStopHostedService(
        IOptions<AzurePostgresAutoSleepOptions> options,
        IDbActivityTracker activity,
        IPostgresLifecycleClient lifecycle,
        IHostApplicationLifetime lifetime,
        ILogger<ShutdownStopHostedService> logger,
        TimeProvider? clock = null,
        IRevisionAwarenessProvider? revisions = null)
    {
        _options = options.Value;
        _activity = activity;
        _lifecycle = lifecycle;
        _lifetime = lifetime;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _revisions = revisions;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Enabled && _options.StopOnShutdown)
        {
            _registration = _lifetime.ApplicationStopping.Register(OnStopping);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _registration.Dispose();

    private void OnStopping()
    {
        // ApplicationStopping handlers run synchronously and the host waits for them; driving the bounded
        // async stop to completion here is what keeps the process alive long enough to sleep the DB.
        using var cts = new CancellationTokenSource(_options.ShutdownStopTimeout);
        EvaluateShutdownAsync(cts.Token).GetAwaiter().GetResult();
    }

    internal async Task EvaluateShutdownAsync(CancellationToken ct)
    {
        var idleFor = _clock.GetUtcNow() - _activity.LastActivity;
        if (idleFor < _options.IdleThreshold)
        {
            _logger.LogDebug(
                "Shutdown stop skipped: DB active {IdleSeconds:F0}s ago (< {ThresholdSeconds:F0}s threshold).",
                idleFor.TotalSeconds,
                _options.IdleThreshold.TotalSeconds);
            return;
        }

        if (_revisions is not null)
        {
            try
            {
                if (await _revisions.IsDeployInProgressAsync(ct).ConfigureAwait(false))
                {
                    _logger.LogInformation("Shutdown stop skipped: another revision is deploying.");
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Revision check failed during shutdown; proceeding to stop.");
            }
        }

        try
        {
            await _lifecycle.StopAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Stopped Postgres flexible server on shutdown after {IdleSeconds:F0}s idle.",
                idleFor.TotalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Shutdown stop did not complete within {TimeoutSeconds:F0}s; letting the process exit.",
                _options.ShutdownStopTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Shutdown stop failed; letting the process exit.");
        }
    }
}
