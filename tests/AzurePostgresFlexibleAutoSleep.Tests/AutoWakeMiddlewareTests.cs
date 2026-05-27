using AzurePostgresFlexibleAutoSleep.Activity;
using AzurePostgresFlexibleAutoSleep.Lifecycle;
using AzurePostgresFlexibleAutoSleep.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzurePostgresFlexibleAutoSleep.Tests;

public class AutoWakeMiddlewareTests
{
    private static (AutoWakeMiddleware mw, FakePostgresLifecycleClient lifecycle, DbActivityTracker tracker, int[] nextCount)
        Build(AzurePostgresAutoSleepOptions options, FakePostgresLifecycleClient lifecycle)
    {
        var tracker = new DbActivityTracker();
        var nextCount = new int[1];
        var mw = new AutoWakeMiddleware(
            ctx => { nextCount[0]++; return Task.CompletedTask; },
            Options.Create(options),
            lifecycle,
            tracker,
            NullLogger<AutoWakeMiddleware>.Instance);
        return (mw, lifecycle, tracker, nextCount);
    }

    private static AzurePostgresAutoSleepOptions DefaultOptions() => new()
    {
        ResourceId = "/subscriptions/x/resourceGroups/y/providers/Microsoft.DBforPostgreSQL/flexibleServers/z",
    };

    private static HttpContext NewContext(string path = "/api/things")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Fact]
    public async Task Exempt_path_passes_through_without_calling_lifecycle()
    {
        var opts = DefaultOptions();
        opts.ExemptPaths = new() { "/healthz" };
        var (mw, lifecycle, _, nextCount) = Build(opts, new FakePostgresLifecycleClient());

        await mw.InvokeAsync(NewContext("/healthz"));

        Assert.Equal(1, nextCount[0]);
        Assert.Equal(0, lifecycle.EnsureAwakeCalls);
    }

    [Fact]
    public async Task State_ready_passes_through_calling_ensure_awake_which_is_noop_in_real_client()
    {
        var (mw, lifecycle, tracker, nextCount) = Build(DefaultOptions(),
            new FakePostgresLifecycleClient { State = PostgresServerState.Ready });

        var before = tracker.LastActivity;
        await Task.Delay(2);
        await mw.InvokeAsync(NewContext());

        Assert.Equal(1, nextCount[0]);
        Assert.Equal(1, lifecycle.EnsureAwakeCalls);
        Assert.True(tracker.LastActivity > before);
    }

    [Fact]
    public async Task State_stopped_triggers_ensure_awake_then_next()
    {
        var (mw, lifecycle, _, nextCount) = Build(DefaultOptions(),
            new FakePostgresLifecycleClient { State = PostgresServerState.Stopped });

        await mw.InvokeAsync(NewContext());

        Assert.Equal(1, lifecycle.EnsureAwakeCalls);
        Assert.Equal(1, nextCount[0]);
        Assert.Equal(PostgresServerState.Ready, lifecycle.State);
    }

    [Fact]
    public async Task Arm_request_failure_returns_503()
    {
        var (mw, _, _, nextCount) = Build(DefaultOptions(),
            new FakePostgresLifecycleClient
            {
                State = PostgresServerState.Stopped,
                EnsureAwakeException = new Azure.RequestFailedException(403, "auth failed"),
            });
        var ctx = NewContext();

        await mw.InvokeAsync(ctx);

        Assert.Equal(0, nextCount[0]);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ctx.Response.StatusCode);
        Assert.Equal("60", ctx.Response.Headers.RetryAfter);
    }

    [Fact]
    public async Task Dropping_resource_returns_503_not_500()
    {
        var (mw, _, _, nextCount) = Build(DefaultOptions(),
            new FakePostgresLifecycleClient
            {
                State = PostgresServerState.Dropping,
                EnsureAwakeException = new InvalidOperationException("being deleted"),
            });
        var ctx = NewContext();

        await mw.InvokeAsync(ctx);

        Assert.Equal(0, nextCount[0]);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Client_disconnect_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (mw, _, _, _) = Build(DefaultOptions(),
            new FakePostgresLifecycleClient
            {
                State = PostgresServerState.Stopped,
                EnsureAwakeException = new OperationCanceledException(cts.Token),
            });
        var ctx = NewContext();
        ctx.RequestAborted = cts.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mw.InvokeAsync(ctx));
    }

    [Fact]
    public async Task Wake_timeout_returns_503_with_retry_after()
    {
        var (mw, _, _, nextCount) = Build(DefaultOptions(),
            new FakePostgresLifecycleClient
            {
                State = PostgresServerState.Stopped,
                EnsureAwakeException = new TimeoutException("nope"),
            });
        var ctx = NewContext();

        await mw.InvokeAsync(ctx);

        Assert.Equal(0, nextCount[0]);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ctx.Response.StatusCode);
        Assert.Equal("60", ctx.Response.Headers.RetryAfter);
    }

    [Fact]
    public async Task Concurrent_requests_during_cold_start_collapse_to_one_start()
    {
        var lifecycle = new FakePostgresLifecycleClient
        {
            State = PostgresServerState.Stopped,
            EnsureAwakeDelay = TimeSpan.FromMilliseconds(100),
        };
        var (mw, _, _, nextCount) = Build(DefaultOptions(), lifecycle);

        var serialize = new SemaphoreSlim(1, 1);
        var originalLifecycle = lifecycle;
        var wrapped = new SerializingLifecycle(originalLifecycle, serialize);
        var mw2 = new AutoWakeMiddleware(
            ctx => { nextCount[0]++; return Task.CompletedTask; },
            Options.Create(DefaultOptions()),
            wrapped,
            new DbActivityTracker(),
            NullLogger<AutoWakeMiddleware>.Instance);

        var tasks = Enumerable.Range(0, 5).Select(_ => mw2.InvokeAsync(NewContext())).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, originalLifecycle.EnsureAwakeCalls);
        Assert.Equal(5, nextCount[0]);
    }

    [Fact]
    public async Task Wake_logs_method_and_path_at_information()
    {
        var opts = DefaultOptions();
        var logger = new ListLogger<AutoWakeMiddleware>();
        var lifecycle = new FakePostgresLifecycleClient { State = PostgresServerState.Stopped };
        var mw = new AutoWakeMiddleware(
            _ => Task.CompletedTask,
            Options.Create(opts),
            lifecycle,
            new DbActivityTracker(),
            logger);

        var ctx = NewContext("/api/things");
        ctx.Request.Method = "POST";
        await mw.InvokeAsync(ctx);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("POST") &&
            e.Message.Contains("/api/things"));
    }

    [Fact]
    public async Task Exempt_path_does_not_log_wake_trigger()
    {
        var opts = DefaultOptions();
        opts.ExemptPaths = new() { "/healthz" };
        var logger = new ListLogger<AutoWakeMiddleware>();
        var mw = new AutoWakeMiddleware(
            _ => Task.CompletedTask,
            Options.Create(opts),
            new FakePostgresLifecycleClient(),
            new DbActivityTracker(),
            logger);

        await mw.InvokeAsync(NewContext("/healthz"));

        Assert.DoesNotContain(logger.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains("Wake"));
    }

    [Fact]
    public async Task ExemptPredicate_returning_true_skips_wake()
    {
        var opts = DefaultOptions();
        opts.ExemptPredicate = ctx => !ctx.Request.Path.StartsWithSegments("/api");
        var lifecycle = new FakePostgresLifecycleClient { State = PostgresServerState.Stopped };
        var (mw, _, _, nextCount) = Build(opts, lifecycle);

        await mw.InvokeAsync(NewContext("/admin"));

        Assert.Equal(1, nextCount[0]);
        Assert.Equal(0, lifecycle.EnsureAwakeCalls);
    }

    [Fact]
    public async Task ExemptPredicate_returning_false_still_wakes()
    {
        var opts = DefaultOptions();
        opts.ExemptPredicate = ctx => !ctx.Request.Path.StartsWithSegments("/api");
        var lifecycle = new FakePostgresLifecycleClient { State = PostgresServerState.Stopped };
        var (mw, _, _, nextCount) = Build(opts, lifecycle);

        await mw.InvokeAsync(NewContext("/api/things"));

        Assert.Equal(1, nextCount[0]);
        Assert.Equal(1, lifecycle.EnsureAwakeCalls);
    }

    [Fact]
    public async Task ExemptPaths_and_ExemptPredicate_compose_as_OR()
    {
        var opts = DefaultOptions();
        opts.ExemptPaths = new() { "/healthz" };
        opts.ExemptPredicate = ctx => ctx.Request.Headers.ContainsKey("X-Skip-Wake");
        var lifecycle = new FakePostgresLifecycleClient { State = PostgresServerState.Stopped };
        var (mw, _, _, _) = Build(opts, lifecycle);

        await mw.InvokeAsync(NewContext("/healthz"));
        var ctx = NewContext("/api/things");
        ctx.Request.Headers["X-Skip-Wake"] = "1";
        await mw.InvokeAsync(ctx);

        Assert.Equal(0, lifecycle.EnsureAwakeCalls);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class SerializingLifecycle : IPostgresLifecycleClient
    {
        private readonly IPostgresLifecycleClient _inner;
        private readonly SemaphoreSlim _gate;

        public SerializingLifecycle(IPostgresLifecycleClient inner, SemaphoreSlim gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public Task<PostgresServerState> GetStateAsync(CancellationToken ct = default) => _inner.GetStateAsync(ct);

        public async Task EnsureAwakeAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (await _inner.GetStateAsync(ct) == PostgresServerState.Ready) return;
                await _inner.EnsureAwakeAsync(ct);
            }
            finally { _gate.Release(); }
        }

        public Task StopAsync(CancellationToken ct = default) => _inner.StopAsync(ct);
    }
}
