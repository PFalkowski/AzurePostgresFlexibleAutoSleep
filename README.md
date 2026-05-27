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
| `Credential` | `DefaultAzureCredential()` | Override the ARM client credential (e.g. to inject a test fake). |

### Wake at startup (EF migrations, seed loaders)

If your app touches the DB in `Program.cs` before `app.Run()` — e.g. `await db.Database.MigrateAsync()` — the request-pipeline middleware can't help: the call happens before any HTTP request. Opt in to a startup-time wake so the container doesn't crash-loop when restarted while the DB is stopped:

```csharp
builder.Services
    .AddAzurePostgresAutoSleep(opts => { opts.ResourceId = "..."; })
    .WakeOnApplicationStartup();   // or: opts.WakeOnStartup = true;
```

The wake runs in `StartAsync` of an `IHostedService` registered before `AutoStopHostedService`. If it exceeds `StartupWakeTimeout` or the ARM call fails, the host startup fails fast — the platform restart-backoff is a better recovery path than a hung process.

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
