# Changelog

All notable changes to this project are documented here. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows [SemVer](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] - 2026-05-27

### Added
- `StartupWakeHostedService` + `WakeOnStartup` / `StartupWakeTimeout` options + `WakeOnApplicationStartup()` fluent helper. Wakes the DB during host startup, before any other `IHostedService`, so EF migrations / seed loaders that run before `app.Run()` don't crash-loop when the platform restarts the container while Postgres is `Stopped` (#2).
- `ExemptPredicate` (`Func<HttpContext, bool>?`) on `AzurePostgresAutoSleepOptions`, composing with `ExemptPaths` via OR. Cleanly expresses "exempt anything not under `/api`" for SPA hosts (#6).
- `PostgresAutoSleepHealthCheck` (`IHealthCheck`) treating `Stopped` as `Healthy`, `Starting` / `Stopping` as `Degraded`, `Dropping` / `Failed` / `Unknown` as `Unhealthy`. Register via `AddHealthChecks().AddAzurePostgresAutoSleepHealthCheck()` (#5).

### Changed
- `AutoWakeMiddleware` logs `Wake triggered by {Method} {Path}` at Information before each non-exempt request, so unexpected wakes are diagnosable from logs alone (#3).
- README: new "Common pitfalls" section covering the `MapFallbackToFile` routing trap, Always On warning, wake-trigger log, and `ExemptPredicate` usage; new "Health checks" section; `ExemptPaths` semantics spelled out in the options table and XML doc (#4).

## [0.1.1] - 2026-05-26

### Fixed
- `AutoWakeMiddleware` now returns `503` (not `500`) when the wake actually times out. The `WaitForAsync` timeout path was previously unreachable because the cancellation token threw `OperationCanceledException` ahead of the explicit `TimeoutException`.
- `AutoWakeMiddleware` also translates `Azure.RequestFailedException` (auth/throttling/transient ARM errors) and the "resource is dropping" `InvalidOperationException` into `503 Service Unavailable` with `Retry-After: 60`. Client disconnects (`context.RequestAborted`) are no longer swallowed.
- `Microsoft.DBforPostgreSQL/flexibleServers` state `Updating` now maps to `Starting` so the middleware waits for the server to become `Ready` instead of forwarding traffic to a transitioning instance.
- `Microsoft.DBforPostgreSQL/flexibleServers` state `Dropping` maps to a new `PostgresServerState.Dropping`; `EnsureAwakeAsync` throws instead of attempting to start a resource that is being deleted.
- Options validation now also rejects non-positive `WakePollInterval`, `StopCheckInterval`, and `StateCacheLifetime`.
- `ActivityCommandInterceptor` now records activity on `CommandFailed` / `CommandFailedAsync` so a workload that is actively talking to the DB but receiving errors no longer trips the idle stop.

## [0.1.0] - 2026-05-26

### Added
- `AddAzurePostgresAutoSleep(...)` / `UseAzurePostgresAutoSleep()` extensions for ASP.NET Core hosts.
- `AutoWakeMiddleware`: starts the configured Azure Postgres Flexible Server on incoming requests; returns `503` with `Retry-After: 60` on wake timeout.
- `AutoStopHostedService`: background loop that stops the server after `IdleThreshold` of continuous inactivity.
- `IDbActivityTracker` + `DbActivityTracker` — Interlocked-based last-activity timestamp.
- `ActivityCommandInterceptor` — EF Core `DbCommandInterceptor` recording activity on Reader/NonQuery/Scalar execution.
- `IDbWaker` + `DbWaker` — explicit wake API for background jobs that bypass HTTP middleware.
- `PostgresLifecycleClient` — `ArmClient`-backed wrapper around the Flexible Server start/stop ARM verbs with state caching and serialized transitions.
- Sample web API under `samples/SampleWebApi/` showing EF interceptor + `IDbWaker` wiring.
- Threat model under `docs/threat-model.md` documenting blast radius and the custom Azure role.

[Unreleased]: https://github.com/PFalkowski/AzurePostgresFlexibleAutoSleep/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/PFalkowski/AzurePostgresFlexibleAutoSleep/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/PFalkowski/AzurePostgresFlexibleAutoSleep/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/PFalkowski/AzurePostgresFlexibleAutoSleep/releases/tag/v0.1.0
