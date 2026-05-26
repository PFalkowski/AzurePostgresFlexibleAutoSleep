# Changelog

All notable changes to this project are documented here. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows [SemVer](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/PFalkowski/AzurePostgresFlexibleAutoSleep/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/PFalkowski/AzurePostgresFlexibleAutoSleep/releases/tag/v0.1.0
