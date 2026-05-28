# AzurePostgresFlexibleAutoSleep

[![ci](https://github.com/PFalkowski/AzurePostgresFlexibleAutoSleep/actions/workflows/ci.yml/badge.svg)](https://github.com/PFalkowski/AzurePostgresFlexibleAutoSleep/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/AzurePostgresFlexibleAutoSleep.svg)](https://www.nuget.org/packages/AzurePostgresFlexibleAutoSleep/)

ASP.NET Core middleware that **stops** an Azure Postgres Flexible Server after a configurable idle period and **starts** it on-demand when a request that needs the database arrives. Cuts the ~$10/mo compute slice of a B1ms server by 80%+ for low-traffic apps, at the cost of a 60–90 s cold start on the first request after idle.

## Install

```bash
dotnet add package AzurePostgresFlexibleAutoSleep
```

Target: `net8.0`.

## Quick start

```csharp
using AzurePostgresFlexibleAutoSleep;
using AzurePostgresFlexibleAutoSleep.Activity;
using AzurePostgresFlexibleAutoSleep.DependencyInjection;

builder.Services.AddAzurePostgresAutoSleep(opts =>
{
    opts.ResourceId    = "/subscriptions/.../flexibleServers/psql-mydb";
    opts.IdleThreshold = TimeSpan.FromMinutes(15);
    opts.ExemptPaths   = new() { "/healthz", "/api/purchase/webhook" };
});

builder.Services.AddDbContext<AppDbContext>((sp, opt) =>
    opt.UseNpgsql(connStr)
       .AddInterceptors(sp.GetRequiredService<ActivityCommandInterceptor>()));

var app = builder.Build();

app.UseAzurePostgresAutoSleep();   // before UseRouting / UseAuthentication
app.UseRouting();
// ... rest of pipeline
app.Run();
```

Background-job usage (request never enters the middleware):

```csharp
public class NightlyJob(IDbWaker waker, AppDbContext db) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await waker.EnsureAwakeAsync(ct);
        _ = await db.Foos.ToListAsync(ct);
    }
}
```

A fuller example lives under [`samples/SampleWebApi/`](samples/SampleWebApi).

## Configuration

| Option | Default | Purpose |
|---|---|---|
| `Enabled` | `true` | Master switch. Set `false` to disable without removing the package. |
| `ResourceId` | _(required)_ | Full Azure Resource ID of the Flexible Server. |
| `IdleThreshold` | `00:15:00` | Stop the DB after this much continuous inactivity. |
| `WakeTimeout` | `00:02:00` | Max time the middleware waits for a wake before returning `503`. |
| `WakePollInterval` | `00:00:05` | Polling interval while waiting for the DB to reach `Ready`. |
| `StopCheckInterval` | `00:01:00` | How often `AutoStopHostedService` evaluates the idle condition. |
| `StateCacheLifetime` | `00:00:30` | TTL of the cached DB state, used to limit ARM API call rate. |
| `ExemptPaths` | `["/healthz"]` | Path prefixes that should NOT trigger a wake (segment-prefix match, case-insensitive). Add webhook endpoints, static assets, etc. See "Common pitfalls" below. |
| `ExemptPredicate` | `null` | Optional `Func<HttpContext,bool>` that composes with `ExemptPaths` via OR. Use for "exempt anything not under `/api`" patterns common to SPA hosts. |
| `WakeOnStartup` | `false` | Wake the DB during host startup, before any other `IHostedService` runs. Prevents crash-loops when EF migrations / seed loaders run while the DB is `Stopped`. |
| `StartupWakeTimeout` | `00:02:00` | Max time the startup wake waits before failing fast. |
| `StopOnShutdown` | `false` | Stop the DB on graceful host shutdown if it has been idle past `IdleThreshold`. For hosts that scale to zero, where the polling auto-stop loop dies with the last replica. See "Hosts that scale to zero". |
| `ShutdownStopTimeout` | `00:00:25` | Max time the shutdown handler waits for the stop to be accepted. Keep below the host's termination grace period (ACA default 30s). |
| `Credential` | `DefaultAzureCredential()` | Override the ARM client credential (e.g. to inject a test fake). |

### Wake at startup (EF migrations, seed loaders)

If your app touches the DB in `Program.cs` before `app.Run()` — e.g. `await db.Database.MigrateAsync()` — the request-pipeline middleware can't help: the call happens before any HTTP request. Opt in to a startup-time wake so the container doesn't crash-loop when restarted while the DB is stopped:

```csharp
builder.Services
    .AddAzurePostgresAutoSleep(opts => { opts.ResourceId = "..."; })
    .WakeOnApplicationStartup();   // or: opts.WakeOnStartup = true;
```

The wake runs in `StartAsync` of an `IHostedService` registered before `AutoStopHostedService`. If it exceeds `StartupWakeTimeout` or the ARM call fails, the host startup fails fast — the platform restart-backoff is a better recovery path than a hung process.

## Hosts that scale to zero

`AutoStopHostedService` is a polling loop: it only stops the DB while the host is alive. On hosts that **scale to zero** (Azure Container Apps consumption plan, AWS App Runner `min=0`, Cloud Run at idle), the last replica is torn down when traffic stops, the loop dies with it, and an idle DB never gets stopped — so the compute saving evaporates on exactly the cheapest topology.

`StopOnShutdown` plugs the common path. On graceful shutdown it stops the DB if it has been idle past `IdleThreshold`:

```csharp
builder.Services.AddAzurePostgresAutoSleep(opts =>
{
    opts.ResourceId          = "...";
    opts.IdleThreshold       = TimeSpan.FromMinutes(15);
    opts.StopOnShutdown      = true;                     // default false
    opts.ShutdownStopTimeout = TimeSpan.FromSeconds(25); // < termination grace period
});
```

The handler registers against `IHostApplicationLifetime.ApplicationStopping` (not `BackgroundService.StopAsync`, which runs too early — before dependent services are usable). The stop is issued with `WaitUntil.Started`, so it returns once Azure *accepts* the request (~1–2s); the realistic shutdown cost is a few seconds, well inside ACA's 30s default grace.

### Caveats — read before enabling

- **It patches the common path, not the gap.** A `SIGKILL` without grace, an OOM, or a host crash bypasses `ApplicationStopping` entirely → the DB stays up until the next graceful shutdown or the next replica's idle loop catches it. `StopOnShutdown` is a cost optimisation, not a guarantee.
- **Don't combine with `WakeOnStartup` on scale-to-zero.** The danger is not the per-burst wake/stop (that's the intended behaviour) — it's *overlapping* lifecycles: a rolling redeploy, a rapid `0→1→0→1`, or `replicas > 1`. The departing replica's shutdown stop puts the DB into `Stopping`; the arriving replica's startup wake then hits `EnsureAwakeAsync`, which for a `Stopping` server waits out the **entire stop, then the entire start** (~2–3 min). That blows `StartupWakeTimeout` (default 2 min), and because `WakeOnStartup` fails fast, the host crashes and the platform restarts it — a crash-loop while the DB churns `start ↔ stop`. Prefer `StopOnShutdown` **alone**: the request-path middleware then wakes lazily without blocking startup and returns `503 + Retry-After` instead of crashing. If you genuinely need both, either register an `IRevisionAwarenessProvider` (so the departing replica doesn't stop during a deploy) or raise `StartupWakeTimeout` above stop+start (~3–4 min) to turn the crash-loop into a slow-but-successful startup.
- **Set the grace period.** `ShutdownStopTimeout` must be below the host's termination grace (ACA `terminationGracePeriodSeconds`, default 30s). On tight grace windows, extend the platform setting.
- **Redeploy looks like scale-in.** From inside the container, a rolling redeploy and a scale-in both deliver `SIGTERM`. The idle gate catches the common case (active workload + SIGTERM ≈ deploy). If an *idle* redeploy stops the DB, the next replica restarts it — a bounded, self-healing ~60–90s delay. To eliminate it, register an `IRevisionAwarenessProvider` (see below).
- **Wake/stop race across replicas.** If a request lands on a new replica just before the old replica's shutdown stop, the two ARM calls race. Azure serializes them; worst case is `started → stopped → started` over ~90s — bounded and self-healing.

### Tightening deploy detection — `IRevisionAwarenessProvider`

`StopOnShutdown` consults an optional `IRevisionAwarenessProvider` (if one is registered) before stopping; when it reports a deploy in progress, the handler is a no-op. No implementation ships in this package — the seam exists so platform-specific detection (e.g. an ACA revision-list check, App Runner `AWS_APPRUNNER_DEPLOYMENT_ID`, Cloud Run `K_REVISION`) can be added without an API break. A built-in provider would need ARM permissions on the *host* resource, beyond the single-DB role this library is scoped to, so it is intentionally left to the consumer.

### Operator alternative — platform dead-man's switch

If you want correct scale-to-zero without relying on the in-process handler, run the stop decision on always-on infrastructure instead: an **Azure Monitor metric alert** on the server's `active_connections` (e.g. `== 0 for 15 min`) wired through an action group to a Logic App / Automation runbook / Function that calls `flexibleServers/stop`. This is external infrastructure (deliberately out of scope for this library), but it survives crashes and scale-in that bypass the graceful-shutdown hook. It composes with `StopOnShutdown` rather than replacing it.

## Required Azure role

The app's identity needs three actions on the single Flexible Server resource. Use a **custom role** scoped to that resource:

```hcl
resource "azurerm_role_definition" "postgres_auto_sleep" {
  name        = "postgres-auto-sleep"
  scope       = azurerm_postgresql_flexible_server.main.id
  description = "Start/stop a single Postgres Flexible Server."

  permissions {
    actions = [
      "Microsoft.DBforPostgreSQL/flexibleServers/start/action",
      "Microsoft.DBforPostgreSQL/flexibleServers/stop/action",
      "Microsoft.DBforPostgreSQL/flexibleServers/read",
    ]
    not_actions = []
  }

  assignable_scopes = [azurerm_postgresql_flexible_server.main.id]
}

resource "azurerm_role_assignment" "app_to_postgres_sleep" {
  scope              = azurerm_postgresql_flexible_server.main.id
  role_definition_id = azurerm_role_definition.postgres_auto_sleep.role_definition_resource_id
  principal_id       = azurerm_linux_web_app.main.identity[0].principal_id
}
```

See [`docs/threat-model.md`](docs/threat-model.md) for the full security model and blast-radius analysis.

## Health checks

Register the bundled health check to expose Postgres state on `/healthz/ready` (or similar). It treats `Stopped` as **Healthy** — the DB is asleep on purpose; the next request will wake it. This avoids the readiness-probe flap you'd get from wiring `AddNpgSql` against the same DB.

```csharp
using AzurePostgresFlexibleAutoSleep.DependencyInjection;

builder.Services.AddHealthChecks()
    .AddAzurePostgresAutoSleepHealthCheck();   // name: "postgres-autosleep"

app.MapHealthChecks("/healthz/ready");
```

| Server state | Health status |
|---|---|
| `Ready` | `Healthy` |
| `Stopped` | `Healthy` (no traffic; will wake on demand) |
| `Starting` / `Stopping` | `Degraded` |
| `Dropping` / `Failed` / `Unknown` | `Unhealthy` |

This is **not** a replacement for an actual "can I run a query" check — use that on a path that's exempt from wake. Pair it with a `/healthz/live` that doesn't touch the DB.

## Common pitfalls

### `ExemptPaths` and endpoint routing

`ExemptPaths` matches via `PathString.StartsWithSegments` — segment-prefix, case-insensitive. `"/assets"` covers `"/assets/index-foo.js"` but not `"/assets-v2"`. To exempt **only** the literal site root, include `"/"` — that matches exact root only and does not exempt every request.

**Pitfall:** if your host calls `MapControllers` / `MapFallbackToFile` without an explicit `app.UseRouting()`, ASP.NET Core auto-inserts `UseRouting` at the *start* of the pipeline. `UseRouting` matches non-API URLs to your fallback endpoint *before* `UseDefaultFiles` / `UseStaticFiles` get a chance to rewrite them. So `GET /` flows through the wake middleware with `Path == "/"` (not `"/index.html"`), and your exempt list needs to include the literal `"/"`.

For SPA hosts where the client router owns paths like `/admin`, `/login`, `/settings/...` and only `/api/...` actually touches the DB, the cleanest expression is the inverse predicate (see #6):

```csharp
opts.ExemptPredicate = ctx => !ctx.Request.Path.StartsWithSegments("/api");
```

`ExemptPaths` and `ExemptPredicate` compose as OR.

### Always On

App Service `Always On` is on by default for B1+ tiers and pings the application root every ~5 min. Unless you exempt the warmup path, every probe wakes the DB and erases the saving auto-sleep is meant to deliver. Either disable Always On for the auto-sleep slot, or exempt the warmup endpoint explicitly.

### Diagnosing unexpected wakes

The wake middleware logs `Wake triggered by {Method} {Path}` at Information before each non-exempt request reaches the lifecycle client. If you see the DB starting and don't know why, grep production logs for `Wake triggered` — that's the smoking gun.

## Operational notes

- **ARM rate limits.** Azure Resource Manager allows 12,000 reads/hour per subscription. With the defaults above this library consumes ~120 reads/hour. Plenty of headroom; not a concern in practice.
- **Cold start cost.** Expect 60–90 s from `Stopped` to `Ready`. The first request after idle absorbs this; subsequent requests are instant until the next idle window.
- **Single-instance only.** v0.1 does not coordinate across replicas. Run on a single-instance App Service plan, or accept that each replica will independently attempt to stop the DB (the ARM API is idempotent, but it's wasteful).
- **Activity not recorded?** Background work that bypasses both EF Core and the middleware (raw `Npgsql` calls, for instance) won't register as activity. Inject `IDbActivityTracker` and call `RecordActivity()` yourself, or call `IDbWaker.EnsureAwakeAsync()` before the operation.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| `ResourceNotFoundException` from `ArmClient` | Wrong `ResourceId` format. Must be `/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.DBforPostgreSQL/flexibleServers/{name}`. |
| `AuthorizationFailed` on start/stop | Role assignment missing. Apply the terraform snippet above. |
| `CredentialUnavailableException` locally | Run `az login` and select the right subscription, or inject a custom `TokenCredential`. |
| Requests return `503` after idle | `WakeTimeout` exceeded; bump the option if your cold start is consistently slower than 120 s. |
| DB stops immediately after deploy | No activity has been recorded yet. Wire the EF interceptor or call `IDbWaker.EnsureAwakeAsync()` at startup. |

## License

MIT — see [`LICENSE`](LICENSE).
