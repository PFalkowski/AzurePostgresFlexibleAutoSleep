# AzurePostgresFlexibleAutoSleep — implementation plan

This document is the **self-contained brief** for an agent or developer picking up the library implementation cold. It assumes no context from prior conversations.

## What you're building

An ASP.NET Core library that **stops** an Azure Postgres Flexible Server after a configurable period of inactivity and **starts** it on-demand when a request that needs the database arrives.

Goal: cut the ~$10/mo compute slice of a Flexible Server B1ms by 80%+ for low-traffic workloads (single-developer pre-production, hobby projects), without operators having to write their own cron/Functions infrastructure.

NuGet ID: `AzurePostgresFlexibleAutoSleep`
Repo: `github.com/PFalkowski/AzurePostgresFlexibleAutoSleep` (this repo)
License: MIT
Target framework: `net8.0` (multi-target to `net9.0` only when there's a concrete demand)

## Architectural decision (frozen — don't relitigate)

The full rationale is in the consumer project's ADR-0056 (`GeopoliticsSim/docs/adr/0056-postgres-auto-sleep.md`). Summary of what's frozen:

- **In-process model.** Library runs inside the consuming ASP.NET Core app. No separate Azure Function, no GitHub Actions cron, no Front Door.
- **Wake on HTTP request.** Middleware before `UseRouting()` checks DB state on incoming requests and starts the DB if stopped.
- **Stop on idle.** `BackgroundService` polls activity timestamp every minute; stops the DB once `now - lastActivity > IdleThreshold`.
- **Activity tracking via `DbCommandInterceptor`** for EF Core consumers, plus an `IDbWaker` service for background-job consumers who don't go through the middleware.
- **Auth via `DefaultAzureCredential`** by default (works with managed identity in Azure, az-cli locally). Operator can inject a custom `TokenCredential`.
- **Custom Azure role** scoped to the single DB resource — least privilege. The library does NOT create this role; the consumer's terraform does.

Patterns rejected with reasons in the ADR: Azure Function (resource-count overhead), GH Actions cron (drift + firewall), Azure Automation (poor ergonomics), edge gateway wake (cost).

## Repository layout (target)

```
AzurePostgresFlexibleAutoSleep/
├── .github/
│   └── workflows/
│       ├── ci.yml                    # build + test on push/PR
│       └── release.yml               # tag v* → pack + push to nuget.org
├── src/
│   └── AzurePostgresFlexibleAutoSleep/
│       ├── AzurePostgresFlexibleAutoSleep.csproj
│       ├── AzurePostgresAutoSleepOptions.cs
│       ├── DependencyInjection/
│       │   ├── ServiceCollectionExtensions.cs        # AddAzurePostgresAutoSleep()
│       │   └── ApplicationBuilderExtensions.cs       # UseAzurePostgresAutoSleep()
│       ├── Lifecycle/
│       │   ├── IPostgresLifecycleClient.cs
│       │   ├── PostgresLifecycleClient.cs            # ArmClient wrapper
│       │   ├── PostgresServerState.cs                # enum: Unknown/Stopped/Starting/Ready/Stopping
│       │   └── StateCache.cs                         # short-TTL cache to avoid ARM rate limits
│       ├── Activity/
│       │   ├── IDbActivityTracker.cs
│       │   ├── DbActivityTracker.cs                  # Interlocked-based timestamp
│       │   └── ActivityCommandInterceptor.cs         # EF Core DbCommandInterceptor
│       ├── IDbWaker.cs                               # public: background jobs call EnsureAwakeAsync()
│       ├── DbWaker.cs                                # impl that delegates to lifecycle client
│       ├── AutoStopHostedService.cs                  # BackgroundService
│       └── AutoWakeMiddleware.cs
├── tests/
│   └── AzurePostgresFlexibleAutoSleep.Tests/
│       ├── AzurePostgresFlexibleAutoSleep.Tests.csproj
│       ├── DbActivityTrackerTests.cs
│       ├── StateCacheTests.cs
│       ├── AutoStopHostedServiceTests.cs
│       ├── AutoWakeMiddlewareTests.cs
│       └── Fakes/
│           └── FakePostgresLifecycleClient.cs        # for tests — no Azure
├── samples/
│   └── SampleWebApi/
│       ├── SampleWebApi.csproj
│       └── Program.cs                                # minimal API showing wiring
├── docs/
│   └── threat-model.md                               # blast radius if app compromised
├── .gitignore                                        # standard .NET
├── .editorconfig                                     # match dotnet defaults
├── Directory.Build.props                             # shared csproj settings (LangVersion, Nullable, TreatWarningsAsErrors)
├── Directory.Packages.props                          # central package management
├── LICENSE                                           # MIT
├── README.md                                         # user-facing quick start
├── CHANGELOG.md                                      # keep-a-changelog format
└── AzurePostgresFlexibleAutoSleep.sln
```

## Public API (frozen — these names are the contract)

### Options

```csharp
namespace AzurePostgresFlexibleAutoSleep;

public sealed class AzurePostgresAutoSleepOptions
{
    /// <summary>Master switch. Set false to disable without removing the package.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Full Azure Resource ID of the Flexible Server, e.g.
    /// /subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.DBforPostgreSQL/flexibleServers/{name}
    /// </summary>
    public required string ResourceId { get; init; }

    /// <summary>Stop the DB after this much continuous inactivity. Default 15 min.</summary>
    public TimeSpan IdleThreshold { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Max time to wait for a wake to complete before failing the request. Default 120 s.</summary>
    public TimeSpan WakeTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Polling interval while waiting for DB to reach Ready state. Default 5 s.</summary>
    public TimeSpan WakePollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the auto-stop hosted service evaluates the idle condition. Default 1 min.</summary>
    public TimeSpan StopCheckInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Cache lifetime for the queried DB state to limit ARM API call rate. Default 30 s.</summary>
    public TimeSpan StateCacheLifetime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Path prefixes that should NOT trigger a wake. Defaults to ["/healthz"].
    /// Add webhook endpoints, static asset paths, etc. Anything in this list
    /// that needs DB access must call IDbWaker.EnsureAwakeAsync explicitly.
    /// </summary>
    public List<string> ExemptPaths { get; set; } = new() { "/healthz" };

    /// <summary>
    /// Credential used for the ARM API calls. Defaults to DefaultAzureCredential()
    /// when null — works under App Service managed identity and local az CLI.
    /// </summary>
    public TokenCredential? Credential { get; set; }
}
```

### DI extensions

```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAzurePostgresAutoSleep(
        this IServiceCollection services,
        Action<AzurePostgresAutoSleepOptions> configure);
}

public static class ApplicationBuilderExtensions
{
    public static IApplicationBuilder UseAzurePostgresAutoSleep(
        this IApplicationBuilder app);
}
```

### Public services consumers can resolve

```csharp
public interface IDbActivityTracker
{
    DateTimeOffset LastActivity { get; }
    void RecordActivity();
}

public interface IDbWaker
{
    /// <summary>
    /// Ensures the DB is in Ready state. Returns immediately if already Ready;
    /// otherwise starts it and waits up to WakeTimeout. Throws on timeout or
    /// permanent failure.
    /// </summary>
    Task EnsureAwakeAsync(CancellationToken ct = default);
}

public sealed class ActivityCommandInterceptor : DbCommandInterceptor
{
    public ActivityCommandInterceptor(IDbActivityTracker tracker);
    // Override ReaderExecutedAsync, NonQueryExecutedAsync, ScalarExecutedAsync
    // to call tracker.RecordActivity().
}
```

## Consumer usage (must work as documented in README)

```csharp
// Program.cs in the consuming app
using AzurePostgresFlexibleAutoSleep;

builder.Services.AddAzurePostgresAutoSleep(opts =>
{
    opts.ResourceId    = "/subscriptions/.../flexibleServers/psql-mydb";
    opts.IdleThreshold = TimeSpan.FromMinutes(15);
    opts.ExemptPaths   = ["/healthz", "/api/purchase/webhook"];
});

builder.Services.AddDbContext<AppDbContext>((sp, opts) =>
    opts.UseNpgsql(connStr)
        .AddInterceptors(sp.GetRequiredService<ActivityCommandInterceptor>()));

var app = builder.Build();

app.UseAzurePostgresAutoSleep();   // BEFORE UseRouting / UseAuthentication
app.UseRouting();
// ... rest of pipeline
```

Background-job usage:

```csharp
public class NightlyJob(IDbWaker waker, AppDbContext db) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Wait for the scheduled tick, THEN wake the DB before touching it
        await waker.EnsureAwakeAsync(ct);
        await db.Foos.ToListAsync(ct);
    }
}
```

## Internal design notes

### State machine

`PostgresServerState`:
- `Unknown` — never queried, or cache expired and refresh in-flight
- `Stopped` — confirmed stopped
- `Starting` — start API call accepted, polling
- `Ready` — running
- `Stopping` — stop API call accepted, polling
- `Failed` — last operation errored; will be retried on next opportunity

`AutoWakeMiddleware` decision table:

| Cached state | Action |
|---|---|
| Ready | Pass through; do not call ARM |
| Stopped | Acquire start lock, call StartAsync (idempotent), poll, set Ready in cache |
| Starting | Wait on the in-flight start (single SemaphoreSlim across the process) |
| Stopping | Wait for Stopping to complete, then Stop→Stopped→Start path |
| Unknown / Failed | Refresh from ARM; recurse |

### Concurrency

- One `SemaphoreSlim(1,1)` inside `PostgresLifecycleClient` serializes start/stop attempts. Multiple concurrent requests during a cold start all await the same in-flight start.
- `DbActivityTracker` uses `Interlocked.Exchange` on a `long` (UTC ticks). No locks needed for the hot path.
- `StateCache` uses `Volatile.Read` / `Volatile.Write` on a struct; refreshes happen under the lifecycle client's semaphore.

### Failure modes

- **ARM API call fails (transient):** retry with exponential backoff (use `Polly` — already used by GeopoliticsSim's HTTP clients, established pattern). Don't crash the host.
- **WakeTimeout exceeded:** middleware returns `503 Service Unavailable` with `{"error":"database wake timed out"}` and `Retry-After: 60` header. Consumer can override the response via an option (deferred to v0.2).
- **Stop fails:** log warning, keep last-known state in cache, retry next tick.
- **Activity tracker not wired up:** library logs a startup warning ("no DB activity will be recorded — the DB will be stopped after IdleThreshold from app startup"). Doesn't block startup — the consumer might be tracking activity another way.

### ARM API rate limits

Azure Resource Manager allows 12,000 reads/hour per subscription. With `StateCacheLifetime = 30 s` and `StopCheckInterval = 1 min`, worst case is ~120 reads/hour from this library — well inside budget. Document this in README under "Operational notes".

## Implementation order (suggested)

1. **`Directory.Build.props` + `Directory.Packages.props` + .editorconfig + .gitignore + LICENSE.** Get the repo skeleton compiling-clean with `dotnet new sln`.
2. **`AzurePostgresAutoSleepOptions`** — pure POCO, no dependencies. Trivial.
3. **`IDbActivityTracker` + `DbActivityTracker`** — Interlocked-based, unit tests for thread safety.
4. **`PostgresServerState` enum + `StateCache`** — value-type cache with TTL.
5. **`IPostgresLifecycleClient` + `PostgresLifecycleClient`** — wraps `Azure.ResourceManager.PostgreSql.FlexibleServers`. Maps ARM REST responses to `PostgresServerState`. Semaphore for start/stop serialization.
6. **`IDbWaker` + `DbWaker`** — thin shim over the lifecycle client.
7. **`AutoStopHostedService`** — `BackgroundService` loop.
8. **`AutoWakeMiddleware`** — uses `IPostgresLifecycleClient.GetStateAsync`. Tests with a fake.
9. **`ActivityCommandInterceptor`** — calls `tracker.RecordActivity()` from EF interceptor hooks.
10. **DI extensions** — `AddAzurePostgresAutoSleep`, `UseAzurePostgresAutoSleep`.
11. **Sample app** under `samples/SampleWebApi/` showing wiring against a placeholder resource ID.
12. **README** — install, quick start, configuration table, operational notes, threat model link.
13. **GitHub Actions CI** — `dotnet restore && dotnet build -c Release && dotnet test`.
14. **GitHub Actions release** — on `v*` tag, pack + push to NuGet.org using a secret API key.
15. **CHANGELOG** — `0.1.0` row.

Aim for ~300 LOC in `src/` and ~400 LOC in `tests/` for v0.1.

## Testing strategy

**Unit tests** (must exist, must run in CI):
- `DbActivityTrackerTests`
  - Concurrent writers from N threads end with the maximum timestamp.
  - `LastActivity` returns the latest written.
- `StateCacheTests`
  - Within TTL: returns cached value without calling refresh delegate.
  - After TTL: calls refresh delegate exactly once.
  - Concurrent reads during a refresh-in-flight do not stampede.
- `AutoStopHostedServiceTests`
  - When `now - LastActivity > IdleThreshold` AND state is Ready, calls `StopAsync`.
  - When activity is recent, does not call `StopAsync`.
  - When state is already Stopped, does not call `StopAsync` (idempotency).
  - Swallows transient client exceptions and logs without crashing the host.
- `AutoWakeMiddlewareTests`
  - Exempt path → pass through, no ARM call.
  - State Ready → pass through, no `StartAsync`.
  - State Stopped → calls `StartAsync`, then `next`.
  - Wake timeout → returns 503.
  - Two concurrent requests during cold start → exactly one `StartAsync` call.

**Integration tests** (optional, not in CI):
- A `dotnet test --filter Category=Integration` suite that points at a real Flexible Server (resource ID from env var). Documents the cold-start time, validates real ARM behavior. Cost ~$0.40 for an hour. Run manually before each release.

## Acceptance criteria (frozen — copy from ADR-0056)

1. **NuGet uniqueness verified** — searches for "postgres auto sleep" and "postgres idle stop" return 0 hits as of 2026-05-26. Confirmed.
2. Library has unit tests covering the decision table above; CI runs them on every PR.
3. Sample app demonstrates wiring with an EF Core DbContext + an `IDbWaker`-using background job.
4. README covers: install, quick start, configuration table, security model, blast-radius if compromised, troubleshooting (common errors: missing role assignment, wrong resource ID format, credential not found).
5. Custom Azure role definition + assignment terraform snippet is in the README so consumers can copy-paste.
6. First publish is `0.1.0` — explicit pre-1.0 to signal "API may change before 1.0".

## Security & threat model (must be in `docs/threat-model.md`)

Cover:

- **Required Azure permissions.** Custom role with exactly three actions:
  - `Microsoft.DBforPostgreSQL/flexibleServers/start/action`
  - `Microsoft.DBforPostgreSQL/flexibleServers/stop/action`
  - `Microsoft.DBforPostgreSQL/flexibleServers/read`
- **Blast radius if the consuming app is compromised:** attacker gains ability to start/stop the DB. Cannot read data, cannot delete the server, cannot reconfigure firewall, cannot extract backups. Worst-case use is repeated stops = denial of service. Acceptable for pre-prod; production deployments should consider Pattern B from the ADR (out-of-process controller).
- **Credential exposure.** Library does not log credentials. ARM client uses the supplied `TokenCredential` directly. Document the recommended `DefaultAzureCredential` configuration: managed identity in prod, `az login` locally.
- **Stripe-webhook-style endpoints.** Document that webhook endpoints MUST be in `ExemptPaths`. A webhook arriving while the DB is asleep should either return 200 immediately and queue the payload, or return 503 and let the provider retry. The library does not solve this for the consumer — it just stays out of the way.

## CI / release plumbing

`.github/workflows/ci.yml`:
```yaml
name: ci
on: [push, pull_request]
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '8.0.x' }
      - run: dotnet restore
      - run: dotnet build -c Release --no-restore
      - run: dotnet test -c Release --no-build --verbosity normal
```

`.github/workflows/release.yml`:
```yaml
name: release
on:
  push:
    tags: ['v*']
jobs:
  publish:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '8.0.x' }
      - run: dotnet pack src/AzurePostgresFlexibleAutoSleep -c Release -o ./nupkgs -p:Version=${GITHUB_REF_NAME#v}
      - run: dotnet nuget push ./nupkgs/*.nupkg --api-key ${{ secrets.NUGET_API_KEY }} --source https://api.nuget.org/v3/index.json
```

NuGet API key needs to be added to GitHub repo secrets before tagging.

## Out of scope for v0.1 (track as GitHub issues post-publish)

- Custom 503 response body / status code override
- Webhook payload queuing helper (`AddDeferredWebhookProcessor`)
- Multi-instance coordination via Postgres advisory lock (currently per-process; safe on single-instance App Service)
- Multi-target net9.0 / netstandard2.1
- OpenTelemetry instrumentation
- Health check that reports DB state

## Dependencies (pinned versions, central management)

`Directory.Packages.props`:
```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Azure.Identity" Version="1.13.1" />
    <PackageVersion Include="Azure.ResourceManager.PostgreSql" Version="1.2.0" />
    <PackageVersion Include="Microsoft.AspNetCore.Http.Abstractions" Version="2.3.0" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Relational" Version="8.0.10" />
    <PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions" Version="8.0.1" />
    <PackageVersion Include="Microsoft.Extensions.Options" Version="8.0.2" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.2" />
    <PackageVersion Include="Polly" Version="8.5.0" />
    <!-- test -->
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageVersion Include="xunit" Version="2.9.2" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageVersion Include="Microsoft.AspNetCore.TestHost" Version="8.0.10" />
  </ItemGroup>
</Project>
```

Verify these are still current minor/patch versions when you start — bump to latest stable.

## Consumer integration (out of scope for this repo, do not implement here)

Once `0.1.0` is on NuGet.org, the GeopoliticsSim consumer needs:

1. Add `PackageReference Include="AzurePostgresFlexibleAutoSleep" Version="0.1.0"` to `Host.Api.csproj`.
2. Wire it in `Program.cs` per the snippet above. `ResourceId` from `module.postgres.id` in terraform (add an output for it).
3. Add the custom role + role assignment to `infra/terraform/modules/postgres_flexible/main.tf`.
4. Add `/api/purchase/webhook` to `ExemptPaths`.
5. Smoke test: deploy, leave idle for 16 min, hit the app, observe the wake.

That work happens in a follow-up PR on the consumer repo, NOT here.

## Operator answers (resolved 2026-05-26)

- ✅ **GitHub repo:** `github.com/PFalkowski/AzurePostgresFlexibleAutoSleep` — already created, public, MIT-licensed, master branch tracks origin.
- ✅ **Visibility:** public.
- ⚠️ **NuGet account:** owned by `PFalkowski` on nuget.org. The package ID `AzurePostgresFlexibleAutoSleep` is **not yet reserved**. Reserve it as part of step 1 of the implementation order — easiest path is to `dotnet pack` a placeholder `0.0.1-reserve` and `dotnet nuget push` it under the operator's API key, then continue normal development. Do this BEFORE the `release.yml` workflow runs against a `v*` tag.
- ✅ **Initial version:** `0.1.0`.

Start at "Implementation order" step 1. Good luck.
