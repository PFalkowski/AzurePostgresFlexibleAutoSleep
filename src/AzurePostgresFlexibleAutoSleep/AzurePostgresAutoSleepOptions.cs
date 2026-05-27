using Azure.Core;
using Microsoft.AspNetCore.Http;

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

    /// <summary>
    /// Path prefixes that should NOT trigger a wake. Matching uses
    /// <see cref="PathString.StartsWithSegments(PathString)"/> semantics: segment-prefix and
    /// case-insensitive, so <c>"/assets"</c> matches <c>"/assets/index-foo.js"</c> but not
    /// <c>"/assets-v2"</c>. To exempt only the literal site root, include <c>"/"</c> —
    /// this matches exact root only and does not exempt every request.
    /// </summary>
    public List<string> ExemptPaths { get; set; } = new() { "/healthz" };

    /// <summary>
    /// Optional. Runs in addition to <see cref="ExemptPaths"/> — a request is exempt
    /// if either a path prefix matches or this predicate returns true. Use this when
    /// the consumer needs context beyond the path (e.g. "exempt anything not under /api").
    /// </summary>
    public Func<HttpContext, bool>? ExemptPredicate { get; set; }

    public bool WakeOnStartup { get; set; } = false;

    public TimeSpan StartupWakeTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TokenCredential? Credential { get; set; }
}
