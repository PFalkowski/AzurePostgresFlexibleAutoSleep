# Threat model

This library runs **inside your ASP.NET Core process** and calls Azure Resource Manager (ARM) to start/stop a single Postgres Flexible Server. The model below covers what the library can and cannot do, what blast radius an attacker gains if the host app is compromised, and how to keep that radius minimal.

## Required Azure permissions

Grant the app's identity (managed identity in production, your `az login` user locally) a **custom role** scoped to a single Flexible Server resource. Three actions only:

- `Microsoft.DBforPostgreSQL/flexibleServers/start/action`
- `Microsoft.DBforPostgreSQL/flexibleServers/stop/action`
- `Microsoft.DBforPostgreSQL/flexibleServers/read`

Do **not** grant `Contributor` or any built-in role that allows write/delete on the server. The custom role keeps the blast radius tight.

Terraform snippet (copy into your infra repo):

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

## Blast radius if the host app is compromised

An attacker who pops a shell inside your ASP.NET Core process gains the app's identity and therefore the three actions above. Specifically:

| Capability | Granted? |
|---|---|
| Start the DB | ✅ |
| Stop the DB | ✅ |
| Read DB metadata (state, SKU, location) | ✅ |
| Read database **contents** | ❌ (separate Postgres user/role on the connection string) |
| Delete the server | ❌ |
| Reconfigure firewall / networking | ❌ |
| Read or restore backups | ❌ |
| Move to another subscription | ❌ |

Worst-case abuse: repeated **stop** calls = denial of service against your own database. Not a data-exfiltration vector and not lateral movement onto other resources.

This is acceptable for pre-production and hobby workloads. For production, consider Pattern B in ADR-0056 (the out-of-process controller) so the application identity does not carry ARM start/stop permissions at all.

## Credential handling

- The library never logs the `TokenCredential` instance, the access token, or the connection string.
- `DefaultAzureCredential` is the default — managed identity in Azure, `az login` locally. No client secrets in app settings.
- Custom credentials are honored if injected via `AzurePostgresAutoSleepOptions.Credential`. The library uses whatever you pass and does not persist it.

## Webhook-style endpoints

Any endpoint that **must respond on the first request after idle** — Stripe webhooks, GitHub webhooks, payment-provider callbacks — must be added to `ExemptPaths`. The middleware does not wake the DB for exempt paths. Two acceptable patterns:

1. **Queue and ack:** return `200` immediately, queue the payload to durable storage, process after wake on the next non-exempt request. The library does not provide this helper in `v0.1`; you build it yourself.
2. **Let the provider retry:** return `503` and rely on the provider's retry policy. Easier; works for Stripe, GitHub, and most modern webhook senders.

If a webhook endpoint is **not** in `ExemptPaths`, the first request after idle will block on a 60–90 s cold start and likely time out at the provider's edge before the wake completes.

## What this library deliberately does not do

- It does not implement multi-instance coordination. Two app instances will independently try to stop the DB; the ARM API is idempotent on stop, so this is safe but mildly wasteful. Use a single-instance App Service plan for v0.1 deployments.
- It does not enforce least-privilege at runtime. The role assignment in your terraform is the single source of truth — the library cannot stop you from over-granting.
- It does not encrypt or proxy traffic to Postgres. Use TLS on your connection string.
