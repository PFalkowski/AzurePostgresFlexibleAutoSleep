# 0001 — StopOnShutdown: sleep the DB on graceful host shutdown for scale-to-zero hosts

- Status: **Accepted** (Option A; implemented in #11)
- Date: 2026-05-28 (accepted 2026-05-29)
- Driving issue: [#11 StopOnShutdown](https://github.com/PFalkowski/AzurePostgresFlexibleAutoSleep/issues/11)
- Related: consumer `GeopoliticsSim/docs/adr/0056-postgres-auto-sleep.md` (froze the in-process model and the single-resource least-privilege role this ADR must not break), `GeopoliticsSim/docs/adr/0054-aca-migration-evaluation.md` (the ACA topology that surfaced this gap)
- This is the first ADR in this repo; it also establishes the `docs/adr/` convention here.

## Context

`AutoStopHostedService` is a polling `BackgroundService` (`src/AzurePostgresFlexibleAutoSleep/AutoStopHostedService.cs`). Its idle loop only ticks while the .NET host is alive. On hosts that **scale to zero** — Azure Container Apps consumption plan, AWS App Runner `min=0`, Cloud Run at idle — the last replica is torn down when traffic stops, the loop dies with it, and the idle DB is never sent its `StopAsync`. The DB stays in whatever state it held when the host vanished.

This collapses the library's value proposition on exactly the cheapest topology: idle compute is free on scale-to-zero, but the DB silently never sleeps, so the ~$8/mo compute saving (ADR-0056) is lost. Today consumers must pin `min_replicas = 1` (~+$11/mo just to keep the timer alive), run an always-on tier alongside (~+$13/mo), or accept that the DB never sleeps.

Frozen tenets from ADR-0056 this ADR must respect:
- **In-process only** — no separate Function, cron, or external scheduler.
- **Least-privilege role scoped to the single Postgres resource** — start/stop/read on one `flexibleServers/...`, nothing else.
- **Default off** for anything that changes existing behaviour.

## The core tension

Scale-to-zero is fundamentally at odds with the in-process tenet: a process that may not be running cannot run a timer. Any in-process answer is therefore a *patch over the gap*, not a closure of it. The honest framing for the options below: we are choosing how much complexity and coupling to spend buying back a bounded fraction of the saving, knowing the gap can never be fully closed in-process (a `SIGKILL` without grace, an OOM, or a host crash still leaves the DB running until the next graceful shutdown or the next replica's idle loop).

## The shutdown-ordering trap

`BackgroundService.StopAsync` runs **before** `IHostApplicationLifetime.ApplicationStopping` handlers fire. Putting stop-on-shutdown logic inside `AutoStopHostedService.StopAsync` is wrong: by then dependent services (the `ArmClient`/credential inside `PostgresLifecycleClient`, Npgsql data sources) may already be tearing down. The handler must register directly against `IHostApplicationLifetime.ApplicationStopping`, with everything it needs (lifecycle client, activity tracker, options, logger) captured at registration time so it does not depend on a scope that is being disposed.

## The deploy-vs-scale-in ambiguity

From inside the container, ACA scale-in and a rolling redeploy look identical — both deliver `SIGTERM` with a grace window. A naive "stop on shutdown" will sometimes stop the DB during a redeploy. The issue proposed a layered heuristic:

- **Layer 1 — idle gate (always):** if `now - LastActivity < IdleThreshold`, do not stop. Active workload + SIGTERM ≈ deploy. Catches the common "developer deploying while using the app" case.
- **Layer 2 — ACA revision signal:** read `CONTAINER_APP_REVISION` at startup; at shutdown, query ARM for active revisions. If another revision is `Active`/`Provisioning`, a deploy is in progress → do not stop.
- **Layer 3 — fallback:** no platform signal and Layer 1 passed → stop. Worst case is a ~60–90s extra wake on the next request.

## Options considered

### Option A — Layers 1 + 3, with Layer 2 as a pluggable (unimplemented) extension point — RECOMMENDED

Ship `StopOnShutdown` that, on `ApplicationStopping`, stops the DB iff the idle gate passes. Define `IRevisionAwarenessProvider` as an **optional injected** dependency consulted before stopping, but ship **no built-in implementation**. With none registered, the decision is Layer 1 + Layer 3.

**Pros:**
- No new Azure permissions; the single-resource least-privilege role (ADR-0056) is untouched.
- No ACA coupling in the package; no `CONTAINER_APP_REVISION` / revision-list dependency.
- Small grace budget — see below.
- Layer 2 (ACA, App Runner, Cloud Run) can be added later as a separate provider package or consumer-supplied impl **without an API break**.

**Cons:**
- Accepts the redeploy race: an idle redeploy may stop the DB, which the next replica's wake middleware (or `WakeOnStartup`) then restarts → bounded, self-healing ~60–90s delay.

### Option B — Full proposal (Layers 1 + 2 + 3) with a built-in ACA revision provider

As the issue specifies, including the ARM revision-list query at shutdown.

**Pros:** eliminates the deploy/scale-in ambiguity precisely on ACA.

**Cons (decisive):**
- **Breaks least-privilege.** Listing ACA revisions is an ARM read against the **Container App resource** — a different resource type than the single `flexibleServers/...` the role is scoped to. Every consumer would have to widen their managed-identity role beyond the one DB. This directly violates a frozen ADR-0056 tenet.
- **Marginal payoff.** The failure Layer 2 prevents — stopping during a redeploy — is the *same* bounded, self-healing race Layer 3 already declares acceptable. We would pay coupling + permission expansion + shutdown latency to avoid a delay we already tolerate elsewhere.
- ACA coupling baked into the core package.

### Option C — Do not add the hook; document the escape hatch

Keep in-process purity. Document that scale-to-zero consumers should either pin `min=1`, pair the DB with a tiny external stopper, or accept the DB never sleeping.

**Rejected.** Delivers no feature and concedes the cheapest topology. The whole point of the issue is to make ACA scale-to-zero viable.

### Option D — Inverted control: client keep-alive / dead-man's-switch

Flip the data flow. Instead of the host observing activity (`DbCommandInterceptor` updating `LastActivity`) and a co-located timer deciding when to stop, every client of the DB — web replicas, background jobs, dev machines, other services — periodically renews a **lease** ("keep-alive"). When no lease renewal arrives for `X` minutes, the DB is stopped. This is a dead-man's switch: *absence* of signal is the trigger, not presence of idleness.

**What it genuinely improves:**
- **Multi-client / multi-instance is native.** A single shared lease with one expiry authority removes the multi-instance stop race (today's deferred advisory-lock mitigation, honest-risk #3). Whoever renews last holds the DB up; nobody races to stop.
- **Decouples the stopper from the workload.** The thing deciding to stop need not be co-located with the thing using the DB. That is exactly the property scale-to-zero needs.

**Why it does not change this decision:**
- **It does not escape the core constraint.** Pushing "still alive" instead of observing idleness is a data-flow inversion, not a control inversion: *something* must still run the expiry countdown and call `stop` precisely when the signals have stopped — i.e. when scale-to-zero has already torn the host down. A dead-man's switch is only as good as the always-on host watching the lease.
  - If that watcher is **in-process**, the failure mode is identical to today — it dies with the last replica. No improvement over what this ADR already addresses.
  - If the watcher is **the Azure platform** (e.g. an Azure Monitor metric alert on `active_connections == 0 for 15 min` → action group → Logic App/Automation/Function calling `flexibleServers/stop`), it works correctly and is nearly free — but it is **external infrastructure**, the category ADR-0056 froze out (Function/cron/edge-wake all rejected there). It is a legitimate *operator* topology, not something this in-process library can ship.
  - Postgres cannot stop its own Azure compute (no in-DB path to ARM), so there is no purely DB-side form of this.
- **New failure mode: fail-safe-to-stop.** Absence of signal = stop. A network partition between clients and the lease store, or a client-side bug that drops renewals, stops a DB that is actually in use → availability harm, not just a cost leak. The current observe-and-stop model fails the other way (stops *less* than ideal), which is the safer default for a cost-optimisation library.
- **More invasive for consumers.** Clients must actively emit keep-alives; today activity is observed transparently via the interceptor (with `IDbWaker` already covering non-HTTP consumers). The inversion pushes wiring onto every client.

**Verdict:** the keep-alive/dead-man's-switch is the *correct* architecture for the scale-to-zero / multi-client case — but only when hosted on an always-on watcher, which lands it in the external-infrastructure category this project deliberately excludes. It is therefore documented here as the recommended **operator-side** pattern (an Azure Monitor autostop rule) for consumers unwilling to rely on the in-process `StopOnShutdown` patch, not as a library feature. `StopOnShutdown` (Option A) remains the right *in-process* answer; the two are complementary, not competing.

## Decision (Accepted)

**Option A.** Add an opt-in `StopOnShutdown` that registers an `ApplicationStopping` handler gated by `IdleThreshold`. Keep `IRevisionAwarenessProvider` as an optional, unimplemented extension point so platform-specific deploy detection (Layer 2) is addable later without an API break. No new Azure permissions, no ACA coupling.

### Proposed surface

```csharp
builder.Services.AddAzurePostgresAutoSleep(opts =>
{
    opts.ResourceId          = "/subscriptions/.../flexibleServers/psql-...";
    opts.IdleThreshold       = TimeSpan.FromMinutes(15);
    opts.StopOnShutdown      = true;                       // ← new; default false
    opts.ShutdownStopTimeout = TimeSpan.FromSeconds(25);   // ← new; fits ACA's 30s default grace
});
```

`StopOnShutdown` defaults to `false` — no behaviour change for existing consumers. When `true` and the idle gate passes, the `ApplicationStopping` handler calls `StopAsync` and waits up to `ShutdownStopTimeout`. If an `IRevisionAwarenessProvider` is registered and reports a deploy in progress, the handler is a no-op (the Layer 2 seam, dormant by default).

## Grace-window budget

`PostgresLifecycleClient.StopAsync` already issues the stop with `Azure.WaitUntil.Started` (`PostgresLifecycleClient.cs:95`) — it returns once ARM **accepts** the stop (~1–2s), not when the server finishes stopping. So the grace budget is far looser than a "5–15s stop POST" estimate implies:

| Step | Time |
|---|---|
| `ApplicationStopping` handler picked up | <1s |
| `GetStateAsync` (cached) + idle check | <1s |
| `flexibleServers/stop` POST (accept only) | ~1–2s |
| Process exit cleanup | <1s |
| **Total** | **~3–5s, comfortably inside 30s** |

`ShutdownStopTimeout` defaults to 25s as a generous ceiling, not an expected duration. Document the `terminationGracePeriodSeconds` interaction so consumers on tight grace windows extend it.

## Honest risks

1. **Residual gap is real.** `SIGKILL`-without-grace, OOM, or a host crash bypasses `ApplicationStopping` → DB stays up until the next graceful shutdown or the next replica's idle loop catches it. This ADR patches the common path, not the gap. State this in the README.
2. **`WakeOnStartup` + `StopOnShutdown` crash-loop on overlapping lifecycles.** The per-burst wake/stop on a single replica is benign (it's the intended behaviour). The real hazard is *overlap* — a rolling redeploy, a rapid `0→1→0→1`, or `replicas > 1`: the departing replica's shutdown stop puts the server into `Stopping`, and the arriving replica's startup wake hits `EnsureAwakeAsync`, which for a `Stopping` server serializes through the **full stop then full start** (~2–3 min). That exceeds `StartupWakeTimeout` (default 2 min); since `WakeOnStartup` fails fast, the host crashes, the platform restarts it, and the DB churns `start ↔ stop` — a crash-loop, not mere over-waking. Mitigation: prefer `StopOnShutdown` *without* `WakeOnStartup` (the request-path middleware wakes lazily without blocking startup and returns `503 + Retry-After` instead of crashing); if both are required, register an `IRevisionAwarenessProvider` so the departing replica skips the stop during a deploy, or raise `StartupWakeTimeout` above stop+start. Consider a startup warning when both flags are set. Documented in the README.
3. **Race with a wake on another replica.** If a request lands on a new replica milliseconds before the old replica's shutdown handler calls stop, the two ARM calls (start, stop) race. Azure serializes them; worst case is `started → stopped → started` over ~90s — bounded and self-healing. One-line README note. A Postgres advisory lock / KV mutex is a deferred mitigation, not worth it for this revision.
4. **Redeploy race (the Layer-2 gap we are accepting).** An idle redeploy may stop the DB; the next replica restarts it. Bounded ~60–90s. The `IRevisionAwarenessProvider` seam exists precisely so this can be tightened later without an API break.

## Acceptance criteria

- [ ] `opts.StopOnShutdown` defaults to `false` — no behaviour change for existing consumers.
- [ ] When `true` and `IdleThreshold` exceeded, host shutdown calls `flexibleServers/stop` and waits up to `ShutdownStopTimeout`.
- [ ] When `true` and the DB was recently active, host shutdown is a no-op (Layer 1).
- [ ] An optional registered `IRevisionAwarenessProvider` reporting "deploy in progress" makes the handler a no-op (Layer 2 seam); with none registered, behaviour is Layer 1 + Layer 3.
- [ ] Stop-call failures are logged at warning level and do **not** block process exit.
- [ ] Handler registers against `IHostApplicationLifetime.ApplicationStopping`, **not** inside `AutoStopHostedService.StopAsync`, with dependencies captured at registration.
- [ ] Unit tests cover Layer 1 (idle vs active), Layer 3 (stop on idle, no provider), the provider-says-deploy no-op, and the failure-does-not-block-exit path.
- [ ] README gains a "Hosts that scale to zero" section: the feature, the residual gap (#1 above), the `WakeOnStartup` interaction (#2), the grace-window requirement, and the redeploy/wake races (#3, #4).

## Reversal path

`StopOnShutdown` defaults to `false`; removing it from `opts` (or setting it false) reverts entirely. `IRevisionAwarenessProvider` is an interface with no built-in implementation, so dropping it later is a no-op for consumers who never registered one. No data, schema, or role changes.

## Deferred

- Built-in `IRevisionAwarenessProvider` for ACA (`CONTAINER_APP_REVISION` + revision-list), App Runner (`AWS_APPRUNNER_DEPLOYMENT_ID`), Cloud Run (`K_REVISION`) — likely a separate package to keep the core free of platform ARM permissions.
- Advisory-lock / KV mutex to serialize wake-vs-stop across replicas (largely obviated for operators who adopt the Option D dead-man's-switch instead).
- README "operator alternatives" note documenting the Option D Azure Monitor autostop rule (`active_connections == 0` → action group → stop) as the external-infra path for consumers who want correct scale-to-zero without the in-process `StopOnShutdown` patch.
- Integration test against a real B1ms confirming the stop accepts within the grace window.
