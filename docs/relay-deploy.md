# Deploying the relay

The relay runs on Azure Container Apps (Linux, consumption) as one regional **stamp**
(design §5.8, size `lean`). `infra/` describes the stamp in Bicep; `.github/workflows/relay.yml`
builds an image per commit, rolls it onto **staging**, smoke-tests it there, and offers it to
**production**, which waits for a person to approve.

| | Production | Staging |
|---|---|---|
| Container App | `agent-bridge-relay` | `agent-bridge-relay-staging` |
| URL | **`https://relay.bad-duck.com`** (also `https://agent-bridge-relay.icyflower-4f71155d.westus2.azurecontainerapps.io`) | `https://agent-bridge-relay-staging.icyflower-4f71155d.westus2.azurecontainerapps.io` |
| Replicas | 1–1 (always on: hosts hold a socket open) | 0–1 (scales to zero between deploys) |
| Size | 0.25 vCPU, 0.5 GiB | 0.25 vCPU, 0.5 GiB |
| Identity (ACR pull, Key Vault) | `id-agent-bridge-relay` | `id-agent-bridge-relay-staging` |
| Key Vault (`token-key`, `vapid-key`) | `kv-abrelay-prod-wus2` | `kv-abrelay-stg-wus2` |
| Blob store ([below](#blob-store)) | `abblobprodwus2` (made by the first `deploy.ps1 -Production` after it was added) | `abblobstgwus2` |
| Deploys from | `main`, after approval | `main` and pull requests touching the relay |
| GitHub environment / OIDC principal | `production` / `agent-bridge-relay-deploy-production` | `staging` / `agent-bridge-relay-deploy-staging` |

Shared in the stamp: the Container Apps environment `cae-agent-bridge-relay-wus2`, the Log
Analytics workspace `log-agent-bridge-relay-wus2` and the registry `abrelaywus2`. All of it is
in resource group `rg-agent-bridge-relay`, West US 2, subscription **Visual Studio Enterprise
Subscription** (`0e38f344-908e-4ff3-88ac-4d499a1f51f8`, tenant `danswettoutlook`), and all of
it was built from `infra/` alone. The first stamp, in the free-trial subscription, is
retired; see [History](#history).

## Compatibility with the dev relay it replaced

Production first ran the S4/S5 spike (`spikes/s4-relay`, image `s5b-10272017`), not
`src/AgentBridge.Relay`. The product relay was promoted from that code with only namespace
and naming changes, and this branch adds operational behaviour. Compared side by side:

| Area | Spike (live) | Product | Effect on a cutover |
|---|---|---|---|
| Endpoints | `/`, `/healthz`, `/v1/push/key`, `POST /v1/auth/device`, `POST /v1/auth/token`, `/v1/ws` | Same, plus `/readyz`; `/healthz` adds `version` | None: additive |
| Root page | Plain text (live has `ServePwa` off) | Same text without "(spike S4)"; the image has no PWA at all | None. The PWAs are on GitHub Pages |
| Wire frames | `Wire.cs`: hello/challenge/proof/ready, 17-byte routing header, `MaxFrame`, `MaxControl` 4096 | Byte-identical; the proof label is still `agent-bridge-s4-relay-proof` | None |
| Close codes | 4001, 4003, 4008, 4009, 4013, 4029, 1012 | Same values | None. Hosts still stop on 4001/4003 and reconnect on anything else |
| Account tokens | HMAC-SHA256 over `{a,l,e}`, key `Relay:TokenKey` | Same code | **Valid only if the key is carried over.** `deploy.ps1` copies the live key into the production vault |
| Devices | Id = SHA-256 of the device key; nothing stored | Same | None: nothing to migrate |
| Presence | In memory, rebuilt as devices reconnect | Same | Devices reconnect once (1012), as on any restart |
| Web Push | `PushGateway`, VAPID key `Relay:VapidPrivateKey`, push-host allowlist, 30/min/account | Same code | **Subscriptions survive only if the VAPID key is carried over** (a browser subscription is bound to the public key). `deploy.ps1` copies it |
| CORS and origins | `Relay:AllowedOrigins` = `https://danswett.github.io` | Same option and checks | Set the same origin (it is in `regions/westus2.bicepparam`) |
| GitHub sign-in | Device flow through the GitHub App client id | Same | Same client id; nothing for users to do |
| Limits | 200 frames/s per socket, 32 devices per account, 60 sign-in calls/min per IP | Same, plus 120 new sockets/min per IP, 16 KiB request bodies, 10,000 sockets per replica | None for real clients: hosts and the PWA back off 1 s → 30 s |
| Logs | GitHub login, account id, device id prefix and device name per connection | None of those: a random per-socket tag, kind and counts; JSON lines | Operational only |
| Shutdown | Sends 1012 to every socket | Same, and while draining fails `/readyz` and refuses new sockets with 503 | Reconnects land on the new revision |

Checked live without signing in: the spike answers `/healthz`, `/`, `/v1/push/key` (an
87-character key), the CORS preflight for `https://danswett.github.io`, an allowed and a
foreign origin's socket, and 4001 for a bad token exactly as the product does; only
`/readyz` is new (`tools/AgentBridge.RelaySmoke` against the live URL).

**Verdict: compatible.** With `TokenKey`, `VapidPrivateKey`, the GitHub client id and the
allowed origin carried over, existing account tokens (valid to about 26 November),
host profiles, the PWA's sign-in and every push subscription keep working. Nobody has to
sign in again. Each device reconnects once, within seconds, when the revision changes.

If the keys were ever lost or exposed, see [Key rotation](#key-rotation).

At the cutover itself the token key **was rotated** (the copied one had been exposed in a
session's output), so every host and the PWA signed in again once; the VAPID key was carried
over unchanged, so push subscriptions survived.

## Why one replica

Presence and routing are in memory on the replica a device is connected to. Two replicas
behind one ingress would split an account's devices between them, and a host on one would
be unreachable from a phone on the other. So `maxReplicas` is 1 (the type in
`modules/types.bicep` refuses more), revisions are single-mode, and there is no
0%-traffic staging revision on the production app: a second live revision is a second
replica. Staging is its own app instead.

One 0.25 vCPU / 0.5 GiB replica holds the `lean` size: thousands of mostly idle sockets
(design §5.9; the relay caps itself at 10,000). `minReplicas` is 1 because hosts keep a
socket open all the time, so scale-to-zero would never happen anyway, and a cold start
would add several seconds to every reconnect after a deploy.

The path past one replica is design §5.8's home/forward routing: each account homed on
one node by rendezvous hashing, nodes forwarding to each other. Container Apps gives
replicas no stable addresses to forward to, so that step is the `standard` size on AKS
(or Container Apps in a VNet if S9 shows replicas can reach each other). The stamp's
`size` parameter is where that goes; the relay binary does not change.

## The pipeline (`.github/workflows/relay.yml`)

1. **infra**: `az bicep build` of the stamp and every region's parameters.
2. **build** (environment `staging`): `docker build`, push `abrelaywus2.azurecr.io/agent-bridge-relay:<sha>`; later jobs deploy its digest.
3. **staging**: `az containerapp update` to that digest with `Relay__Version=<sha>`, then
   `relay-smoke` against staging with staging's token key: health at the new version,
   readiness, no app code, the push key, CORS and origins, 4001, and two devices of a
   throwaway account that find each other in presence, open a Noise channel, echo a
   message, get "undeliverable" for an absent device, have an off-allowlist push refused,
   and leave presence; and (`--expect-blobs`) a 5 MiB file sealed, uploaded to the blob
   store, refused to another device of the account and to another account, fetched,
   opened and checked by its recipient, deleted, and then not found; then a 10 MiB file
   streamed from disk as a host sends a session bundle ([host-transfer.md](host-transfer.md)),
   fetched, checked and deleted the same way.
4. **production** (`main` only, environment `production`): waits for approval by the
   required reviewer. `infra/roll-production.sh` refuses an app `infra/` has not adopted,
   writes the image it replaces to the job summary, rolls the digest, and the smoke test
   runs again without a token.
5. **rollback**: run the workflow by hand with `rollback_to=<earlier commit SHA>`. Same
   approval, same script, same smoke test.

Every job signs in with GitHub OIDC (`azure/login`, `id-token: write`). There is no
client secret anywhere. Each environment has its own principal:

| Principal | Roles |
|---|---|
| `agent-bridge-relay-deploy-staging` | AcrPush on the registry; Contributor on the staging app; Managed Identity Operator on its identity; Key Vault Secrets User on the **staging** vault; Reader on the resource group |
| `agent-bridge-relay-deploy-production` | Contributor on the production app; Managed Identity Operator on its identity; Reader on the resource group |

Each has one federated credential for its environment. GitHub now presents subjects with the owner's and repository's numeric ids, `repo:danswett@3709749/agent-bridge@1436251473:environment:<name>`, so a renamed or recreated repository of the same name cannot sign in; the plain `repo:danswett/agent-bridge:…` form is refused (AADSTS700213). The
`production` environment allows only `main` and requires `danswett`'s approval. Repository
variables `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID` (the stamp's subscription), and each environment's
`AZURE_CLIENT_ID`, are ids, not secrets. The repository variable `RELAY_DEPLOY=true` turns
the Azure jobs on; without it (a fork, a fresh clone) the workflow only compiles `infra/`.

Infra changes (probes, sizes, secrets, a new region) are not applied by CI. A person runs
`infra/deploy.ps1`, which never rolls an image: it re-states what each app runs now.

## Building a stamp

Everything below runs as the subscription's owner, with
`$env:AZURE_CONFIG_DIR = "$HOME\.azure-agentbridge"`. `deploy.ps1` names the subscription on
every call (`-Subscription`, default the Visual Studio Enterprise one), so the CLI's default
never decides where anything goes.

**Once per tenant**, the OIDC principals (one per GitHub environment). Their roles come from
the stamp, in whatever subscription it is deployed to:

```powershell
foreach ($env in 'staging', 'production') {
  $app = az ad app create --display-name "agent-bridge-relay-deploy-$env" --query appId -o tsv
  az ad sp create --id $app | Out-Null
  az ad app federated-credential create --id $app --parameters (@{
    name = "github-$env"; issuer = 'https://token.actions.githubusercontent.com'
    subject = "repo:danswett@3709749/agent-bridge@1436251473:environment:$env"; audiences = @('api://AzureADTokenExchange')
  } | ConvertTo-Json -Compress)
  gh variable set AZURE_CLIENT_ID --env $env --body $app -R danswett/agent-bridge
}
gh variable set AZURE_TENANT_ID --body (az account show --query tenantId -o tsv) -R danswett/agent-bridge
gh variable set AZURE_SUBSCRIPTION_ID --body <stamp subscription id> -R danswett/agent-bridge
gh variable set RELAY_DEPLOY --body true -R danswett/agent-bridge
```

**A stamp from nothing** (how the current one was built, 2026-10-29). Register the resource
providers (`Microsoft.App`, `Microsoft.ContainerRegistry`, `Microsoft.KeyVault`,
`Microsoft.ManagedIdentity`, `Microsoft.OperationalInsights`, `Microsoft.Storage`) and create the resource group,
then:

```powershell
./infra/deploy.ps1 -WhatIf -Production -BaseOnly
# Pass 1 only: environment, workspace, registry, identities, vaults, role assignments and keys.
# -SeedProductionFrom copies production's keys vault to vault from another stamp (any
# subscription), printing only 8-character SHA-256 prefixes; without it new keys are made.
./infra/deploy.ps1 -Production -BaseOnly -SeedProductionFrom <old production vault>
# An image for the apps' first revisions: import one the old registry already has (same digest),
az acr import --subscription <sub> -n abrelaywus2 --registry <old registry resource id> --source agent-bridge-relay@sha256:<digest> --image agent-bridge-relay:<sha>
# or, with no registry to import from, push one with the SDK (dotnet publish src/AgentBridge.Relay
# -t:PublishContainer -p:ContainerRegistry=abrelaywus2.azurecr.io ...); ACR Tasks may be refused.
./infra/deploy.ps1 -Production -ProductionImage abrelaywus2.azurecr.io/agent-bridge-relay@sha256:<digest> -ProductionVersion <sha> -StagingImage <same> -StagingVersion <sha>
```

That run prints the DNS records the custom domain needs (next section). Once they exist,
running it again adds the domain and its certificate. From then on CI rolls images, and a
plain `./infra/deploy.ps1 -Production -ProductionImage <running digest> -ProductionVersion <sha>`
re-applies infra without rolling anything.

**Adopting a hand-made app** instead (what the first stamp did with the spike's app):
`deploy.ps1` finds the live production app, uses its environment, leaves that environment
untouched until `-Production`, and copies the keys the app signs with into the vault.

## Custom domain

Production is `relay.bad-duck.com`, with an Azure-managed certificate (DigiCert, free,
six months, renewed by the platform). The `bad-duck.com` zone is on Cloudflare; the two
records are DNS only (grey cloud), because the certificate is validated against the CNAME
and Cloudflare's proxy would also cut WebSockets and terminate TLS:

| Type | Name | Value |
|---|---|---|
| CNAME | `relay` | `agent-bridge-relay.icyflower-4f71155d.westus2.azurecontainerapps.io` |
| TXT | `asuid.relay` | the environment's verification id, `9E5A7D37AE5B378B92399BA82D0EEEF5F4674EE876FF3BF49EB6BB4B53A20E60` |

The domain is `customDomain` in the region's parameter file. `deploy.ps1` steps it forward
as DNS allows: it prints both records while they are missing; once they resolve it puts the
hostname on the app (pass 2), then issues the managed certificate and binds it with SNI
(pass 3); after that it re-states the binding. Staging has no custom domain; adding
`customDomain: 'relay-staging.bad-duck.com'` to `staging` and two more records would give it one.

**Renewal** is automatic as long as the CNAME keeps pointing at the app and stays DNS only.
Moving the record, proxying it, or deleting the TXT makes renewal fail silently until the
certificate expires (the current one: 2027-04-29). Check with
`az containerapp env certificate list --subscription 0e38f344-908e-4ff3-88ac-4d499a1f51f8 -g rg-agent-bridge-relay -n cae-agent-bridge-relay-wus2 --managed-certificates-only -o table`.

## History

1. **2026-10-27**, dev relay (spike S4/S5) hand-made in the free-trial subscription
   (`ef8fa0cf-…`, resource group `rg-agent-bridge-relay`): app `agent-bridge-relay` in
   environment `cae-agent-bridge`, registry `agentbridgerelay59731`, at
   `https://agent-bridge-relay.agreeablepebble-329880d4.westus2.azurecontainerapps.io`.
2. **2026-10-29 morning**, cutover in place: `infra/` adopted that app, the product relay
   replaced the spike (revision `agent-bridge-relay--0000006`, build `4ab4e0e`), and the token
   key was rotated (the copied one had been exposed in a session's output). The VAPID key was
   carried over.
3. **2026-10-29**, moved to the Visual Studio Enterprise subscription by redeploying the
   stamp from `infra/` (not `az resource move`), served at `relay.bad-duck.com`. Production's
   token and VAPID keys were copied vault to vault (fingerprints `f747df20`, `3e12c65b`), so a
   token from either relay works on both and push subscriptions carried over. The old stamp
   was left running for anything still pointed at it; see
   [Decommissioning the old stamp](#decommissioning-the-old-stamp).

## Key rotation

Rotate a key when it may have been exposed. Never put key material on a command line, in a
file or in output: generate it in the PowerShell process, send it straight to Key Vault's
REST API, and compare only hash prefixes.

**Token key** (`token-key`: signs account tokens). Every account token becomes invalid: each
host exits with "refused … run sign-in again" and needs `agent-bridge-host sign-in --relay <url>`,
and each PWA needs Sign in again. Push subscriptions are unaffected.

```powershell
$h = @{ Authorization = 'Bearer ' + (az account get-access-token --resource https://vault.azure.net --query accessToken -o tsv) }
$body = @{ value = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)); contentType = 'base64' } | ConvertTo-Json
Invoke-RestMethod -Method Put -Uri 'https://kv-abrelay-prod-wus2.vault.azure.net/secrets/token-key?api-version=7.4' -Headers $h -ContentType 'application/json' -Body $body | Out-Null
$body = $null
# A running replica keeps the value it started with; a new revision reads the new one.
az containerapp update -g rg-agent-bridge-relay -n agent-bridge-relay --revision-suffix "key$(Get-Date -Format MMddHHmm)" --query properties.latestRevisionName -o tsv
```

Then check: `relay-smoke` with `RELAY_SMOKE_TOKEN_KEY` set from the vault (in the process,
never echoed) passes the two-device check, which proves the relay signs with the new key; and
a hello with an old account token closes with 4001.

**VAPID key** (`vapid-key`: signs Web Push). Same procedure with a new P-256 key in PKCS#8,
base64 (`[Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256).ExportPkcs8PrivateKey()`).
Every browser subscription is bound to the old public key, so each PWA has to turn
notifications off and on again; hosts drop the old subscriptions when the push service
answers 410. Accounts and tokens are unaffected.

Staging has its own keys in `kv-abrelay-stg-wus2`; rotating them affects only the smoke test.

## Rollback

- **A bad build:** run `relay.yml` with `rollback_to=<previous SHA>` (the SHA is in the
  production job's summary), and approve it.
- **The move to relay.bad-duck.com**, while the old stamp still exists: it runs the same
  build with the same keys, so pointing clients back is enough. Revert `S4_RELAY` in
  `.github/workflows/pages.yml` and the app's default in `src/AgentBridge.App/App.xaml.cs` to
  `https://agent-bridge-relay.agreeablepebble-329880d4.westus2.azurecontainerapps.io`, and
  `agent-bridge-host sign-in --relay <that url>` on each host. Tokens stay valid; every device
  has to be on the same relay to see the others.

## Hostnames, Pages and hosts

The relay's public name is `relay.bad-duck.com`. `pages.yml` bakes it into the PWA's
`config.js` and Content-Security-Policy (`S4_RELAY`), the Windows app has it as its default,
and each host stores the relay it signed in to in its profile's `relay.txt`. Presence is per
relay, so **every device of an account must use the same one**: a host on the old URL and a
phone on the new one never see each other.

To move the name to another stamp or region later, point the CNAME at the new app (and add
the new environment's `asuid` TXT) after adding the domain there; clients need no change.

## Decommissioning the old stamp

Not done; needs approval. The free-trial subscription `ef8fa0cf-48ab-4dc9-80b3-95923fd029da`
still has, in `rg-agent-bridge-relay`: apps `agent-bridge-relay` (production, always on) and
`agent-bridge-relay-staging`, environment `cae-agent-bridge`, registry `agentbridgerelay59731`,
vaults `kv-abrelay-wus2` and `kv-abrelay-wus2-stg`, identities `id-agent-bridge-relay` and
`id-agent-bridge-relay-staging`, and workspace `log-agent-bridge-relay-wus2`. It costs about
**$19/month** (production replica ≈ $14.30, registry ≈ $5, the rest under $0.10), paid from
the trial's credit.

1. Make sure nothing still uses it: every host signed in to `https://relay.bad-duck.com`
   (`relay.txt`), and `/healthz` on the old URL reporting `"online":0` over a day.
2. Delete the resource group; its role assignments (including the deploy principals') go with it:
   `az group delete --subscription ef8fa0cf-48ab-4dc9-80b3-95923fd029da -n rg-agent-bridge-relay --yes`
3. The two vaults stay soft-deleted for 90 days (their secrets recoverable). To end that
   early: `az keyvault purge --subscription ef8fa0cf-48ab-4dc9-80b3-95923fd029da -n kv-abrelay-wus2` and the same for `kv-abrelay-wus2-stg`.
4. Nothing else in GitHub or Entra refers to the old subscription after this PR.

## Adding a region

Copy `regions/westus2.bicepparam` to `regions/<region>.bicepparam` with new names, create the
resource group, and run `./infra/deploy.ps1 -Region <region> -ResourceGroup <rg>`. Until
home/forward routing exists, an account's devices must all use one region, so a second
region is a separate relay URL, not more capacity behind one name.

## Blob store

Large files (design §4.6, [protocol/blobs.md](protocol/blobs.md)): reply attachments over
256 KiB now, session transfers later. Devices upload and download straight to and from
Azure Storage on URLs the relay signs; no blob byte passes through a relay node.

`infra/modules/blob-store.bicep`, one per app (`deployBlobStore`), next to it:

* A StorageV2 account, **Standard_LRS, hot**, HTTPS and TLS 1.2 only, **shared keys off**
  (`allowSharedKeyAccess: false`, so no account key or service SAS can be used, and none is
  configured anywhere), no public blob access, no soft delete or versioning (a deleted blob
  is gone), and CORS for the PWA's origins (`PUT`, `GET`, `OPTIONS`).
* One private container, `blobs`, and a **lifecycle rule** that deletes every blob a day
  after it was written, whatever else happens, matching the relay's 24-hour tickets. A
  recipient deletes each blob as soon as it has it, so most live seconds.
* For the app's user-assigned identity: **Storage Blob Data Contributor** on the container
  and **Storage Blob Delegator** on the account. The relay gets a user delegation key from
  Entra with them and signs one-blob, one-direction, 30-minute SAS URLs with it.
* Environment for the app: `Relay__Blobs__Backend=azure`, `Relay__Blobs__AccountUrl`,
  `Relay__Blobs__Container`, `Relay__Blobs__ManagedIdentityClientId`. Not a secret among
  them. An app without them (`Backend` empty) says `blobs: null` in `ready`, and every
  client keeps to 256 KiB in its channels.

Staging has its account (`abblobstgwus2`, applied with `./infra/deploy.ps1` on 2026-10-29,
which also registered `Microsoft.Storage` in the subscription). Production gets
`abblobprodwus2` the first time infra is applied with `-Production` after this change, as
any infra change is (a person, not CI):

```powershell
./infra/deploy.ps1 -WhatIf -Production -ProductionImage <running digest> -ProductionVersion <sha>
./infra/deploy.ps1 -Production -ProductionImage <running digest> -ProductionVersion <sha>
```

Until then production's image runs without a blob store, and the PWA (whose Content
Security Policy already allows `https://abblobprodwus2.blob.core.windows.net`, set in
`pages.yml`) falls back to 256 KiB. The role assignments can take a few minutes to apply; a
`blob-put` before then answers `unavailable`.

## Production behaviour in the code

- **Probes:** startup and liveness on `/healthz`, readiness on `/readyz`.
- **Drain:** on SIGTERM the relay marks itself draining (`/readyz` 503, new sockets 503),
  sends every device 1012, and stops within 20 s, inside the 30 s termination grace period.
- **Limits:** 64 KiB frames (`Wire.MaxFrame`), 4 KiB control messages, 200 frames/s per
  socket, 32 devices per account, 30 pushes/min per account, 60 sign-in calls/min and 120
  new sockets/min per client address, 16 KiB request bodies, 10,000 sockets per replica.
  Blobs: 201 MiB stored per blob, 2 GiB per account per rolling 24 hours, 30 puts/min per
  account (`Relay__Blobs__*`). The frame limit counts frames as the relay reads them, in
  fixed one-second windows, so a burst it reads late shares a window with the next one;
  the .NET client (hosts, the Windows app) therefore sends at most 150 frames in any 1.5 s
  (`RelayClient.PaceWindow`), which stays inside the limit while the relay reads up to half
  a second behind.
- **Logs:** JSON to stdout, collected by Log Analytics (30 days, 0.1 GB/day cap). No
  account, device, name, address, token or push endpoint: a socket is a random tag that
  means nothing outside the process. ASP.NET request logs are off.
- **Metrics hook:** meter `AgentBridge.Relay` (connections, connects by kind, disconnects
  by outcome, refusals by code, frames and bytes forwarded, undeliverable, pushes and
  sign-ins by outcome, blob operations by kind and outcome and bytes ticketed), tags limited to `kind`, `outcome` and `code` and held to that by a
  test. Nothing exports it yet; the observability slice (design §5.10) will.

## Cost

West US 2 consumption prices; the subscription's monthly free grant is 180,000 vCPU-s,
360,000 GiB-s and 2 million requests.

| Item | Idle (a few devices) | Modest (≈100 users, 300 devices) |
|---|---|---|
| Production replica, 0.25 vCPU / 0.5 GiB, always on | ≈ $14.30 (an open WebSocket is an in-flight request, so the replica bills at the active rate, not the idle one) | ≈ $14.30 (same replica) |
| Staging, scale to zero | ≈ $0 (a few minutes per deploy, under a cent) | ≈ $0 |
| Registry, Basic | ≈ $5.00 | ≈ $5.00 |
| Log Analytics | $0 (well under the 5 GB/month free ingestion) | $0 |
| Key Vault (2 vaults, secret reads on revision start and every 30 min) | < $0.05 | < $0.05 |
| Requests (sign-in, health; probes are free) | $0 | $0 |
| Egress (first 100 GB/month free) | $0 | ≈ $0 (about 25 GB/month at §5.9's 3-client rate) |
| Blob store, 2 accounts: hot LRS, blobs kept 1–2 days, no fixed cost; large attachments and session transfers, each deleted once fetched | < $0.05 | ≈ $0.10–2 (storage ≈ $0.06 for ≈ 3 GB held; writes ≈ $0.05; the rest is downloads to hosts, free inside the 100 GB with the relay's egress, ≈ $0.087/GB past it) |
| **Total** | **≈ $19/month** | **≈ $19–22/month** |

Levers when it matters: GHCR instead of ACR saves the $5 registry; a second region is
another ≈ $14; the next step up is 0.5 vCPU / 1 GiB (≈ $34) for tens of thousands of sockets.
