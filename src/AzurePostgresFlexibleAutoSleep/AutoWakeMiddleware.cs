using AzurePostgresFlexibleAutoSleep.Activity;
using AzurePostgresFlexibleAutoSleep.Lifecycle;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzurePostgresFlexibleAutoSleep;

public sealed class AutoWakeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AzurePostgresAutoSleepOptions _options;
    private readonly IPostgresLifecycleClient _lifecycle;
    private readonly IDbActivityTracker _activity;
    private readonly ILogger<AutoWakeMiddleware> _logger;

    public AutoWakeMiddleware(
        RequestDelegate next,
        IOptions<AzurePostgresAutoSleepOptions> options,
        IPostgresLifecycleClient lifecycle,
        IDbActivityTracker activity,
        ILogger<AutoWakeMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _lifecycle = lifecycle;
        _activity = activity;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled || IsExempt(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        try
        {
            await _lifecycle.EnsureAwakeAsync(context.RequestAborted).ConfigureAwait(false);
            _activity.RecordActivity();
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Wake timed out for request {Path}.", context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "60";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"database wake timed out\"}").ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private bool IsExempt(PathString path)
    {
        foreach (var prefix in _options.ExemptPaths)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
