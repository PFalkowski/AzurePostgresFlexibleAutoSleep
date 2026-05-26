# AzurePostgresFlexibleAutoSleep

[![ci](https://github.com/PFalkowski/AzurePostgresFlexibleAutoSleep/actions/workflows/ci.yml/badge.svg)](https://github.com/PFalkowski/AzurePostgresFlexibleAutoSleep/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/AzurePostgresFlexibleAutoSleep.svg)](https://www.nuget.org/packages/AzurePostgresFlexibleAutoSleep/)

> **Status: pre-implementation.** This repo currently contains only the implementation plan ([`PLAN.md`](PLAN.md)). No code yet. The plan is self-contained — an agent or developer picking this up cold should be able to execute it without further context.

## What it does

Stops your Azure Postgres Flexible Server after `IdleThreshold` of inactivity and starts it on the next HTTP request that needs it. For low-traffic ASP.NET Core apps (single-developer pre-production, hobby projects), this cuts the ~$10/mo compute slice of a B1ms server by 80%+ at the cost of a 60-90 s cold-start on the first request after idle.

## Quick start (once `0.1.0` is published)

```bash
dotnet add package AzurePostgresFlexibleAutoSleep
```

```csharp
builder.Services.AddAzurePostgresAutoSleep(opts =>
{
    opts.ResourceId    = "/subscriptions/.../flexibleServers/psql-mydb";
    opts.IdleThreshold = TimeSpan.FromMinutes(15);
    opts.ExemptPaths   = ["/healthz", "/api/purchase/webhook"];
});

app.UseAzurePostgresAutoSleep();   // before UseRouting
```

Full configuration table, terraform role snippet, and threat model are in [`PLAN.md`](PLAN.md) — and will move to a proper user-facing README once the library is built.

## Why this exists

See ADR-0056 in the consuming project (`GeopoliticsSim/docs/adr/0056-postgres-auto-sleep.md`) for the full decision record — why in-process middleware over an out-of-process Azure Function, why a NuGet over inlined code, why this saves more than scheduled cron.

## Status of work

| Item | State |
|---|---|
| Architectural decision | ✅ Frozen in ADR-0056 |
| NuGet uniqueness verified | ✅ 0 hits for "postgres auto sleep" / "postgres idle stop" on 2026-05-26 |
| NuGet package ID reserved | ⚠️ Not yet — reserve under PFalkowski's nuget.org account as step 1 |
| Implementation plan | ✅ [`PLAN.md`](PLAN.md) |
| Code | ❌ Not started |
| Tests | ❌ Not started |
| CI | ❌ Not started |
| Published to NuGet | ❌ Not started |

## License

MIT — see [`LICENSE`](LICENSE).
