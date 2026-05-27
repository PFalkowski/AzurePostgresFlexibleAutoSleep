using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzurePostgresFlexibleAutoSleep;

public sealed class StartupWakeHostedService : IHostedService
{
    private readonly IDbWaker _waker;
    private readonly AzurePostgresAutoSleepOptions _options;
    private readonly ILogger<StartupWakeHostedService> _logger;

    public StartupWakeHostedService(
        IDbWaker waker,
        IOptions<AzurePostgresAutoSleepOptions> options,
        ILogger<StartupWakeHostedService> logger)
    {
        _waker = waker;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !_options.WakeOnStartup)
        {
            return;
        }

        _logger.LogInformation(
            "Waking Postgres flexible server at application startup (timeout {TimeoutSeconds:F0}s).",
            _options.StartupWakeTimeout.TotalSeconds);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.StartupWakeTimeout);

        try
        {
            await _waker.EnsureAwakeAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                "Startup wake exceeded {TimeoutSeconds:F0}s; failing fast so the host can restart cleanly.",
                _options.StartupWakeTimeout.TotalSeconds);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup wake failed; failing fast so the host can restart cleanly.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
