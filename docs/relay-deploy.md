# Deploying the relay

The relay runs on Azure Container Apps (Linux, consumption) as one regional **stamp**
(design §5.8, size `lean`). `infra/` describes the stamp in Bicep; `.github/workflows/relay.yml`
builds an image per commit, rolls it onto **staging**, smoke-tests it there, and offers it to
**production**, which waits for a person to approve.

| | Production | Staging |
|---|---|---|
| Container App | `agent-bridge-relay` | `agent-bridge-relay-staging` |
| URL | `https://agent-bridge-relay.agreeablepebble-329880d4.westus2.azurecontainerapps.io` (unchanged) | `https://agent-bridge-relay-staging.agreeablepebble-329880d4.westus2.azurecontainerapps.io` |
| Replicas | 1–1 (always on: hosts hold a socket open) | 0–1 (scales to zero between deploys) |
| Size | 0.25 vCPU, 0.5 GiB | 0.25 vCPU, 0.5 GiB |
| Identity (ACR pull, Key Vault) | `id-agent-bridge-relay` | `id-agent-bridge-relay-staging` |
| Key Vault (`token-key`, `vapid-key`) | `kv-abrelay-wus2` | `kv-abrelay-wus2-stg` |
| Deploys from | `main`, after approval | `main` and pull requests touching the relay |
| GitHub environment / OIDC principal | `production` / `agent-bridge-relay-deploy-production` | `staging` / `agent-bridge-relay-deploy-staging` |

Shared in the stamp (resource group `rg-agent-bridge-relay`, West US 2): the Container
Apps environment, the Log Analytics workspace and the registry `agentbridgerelay59731`.

## Compatibility with the dev relay it replaces

Production today runs the S4/S5 spike (`spikes/s4-relay`, image `s5b-10272017`), not
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

If the keys were ever lost: a new token key makes every host exit with "refused … run
sign-in again" (`agent-bridge-host sign-in --relay <url>` on each machine, and Sign in again in
the PWA); a new VAPID key needs notifications turned off and on again in the PWA.

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
2. **build** (environment `staging`): `docker build`, push `agentbridgerelay59731.azurecr.io/agent-bridge-relay:<sha>`; later jobs deploy its digest.
3. **staging**: `az containerapp update` to that digest with `Relay__Version=<sha>`, then
   `relay-smoke` against staging with staging's token key: health at the new version,
   readiness, no app code, the push key, CORS and origins, 4001, and two devices of a
   throwaway account that find each other in presence, open a Noise channel, echo a
   message, get "undeliverable" for an absent device, have an off-allowlist push refused,
   and leave presence.
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
| `agent-bridge-relay-deploy-production` | Contributor on the production app (granted at the cutover); Managed Identity Operator on its identity; Reader on the resource group |

Each has one federated credential for its environment. GitHub now presents subjects with the owner's and repository's numeric ids, `repo:danswett@3709749/agent-bridge@1436251473:environment:<name>`, so a renamed or recreated repository of the same name cannot sign in; the plain `repo:danswett/agent-bridge:…` form is refused (AADSTS700213). The
`production` environment allows only `main` and requires `danswett`'s approval. Repository
variables `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`, and each environment's
`AZURE_CLIENT_ID`, are ids, not secrets. The repository variable `RELAY_DEPLOY=true` turns
the Azure jobs on; without it (a fork, a fresh clone) the workflow only compiles `infra/`.

Infra changes (probes, sizes, secrets, a new region) are not applied by CI. A person runs
`infra/deploy.ps1`, which never rolls an image: it re-states what each app runs now.

## First-time setup

Done once per subscription, as the subscription's owner, with
`$env:AZURE_CONFIG_DIR = "$HOME\.azure-agentbridge"`:

```powershell
# OIDC principals (one per GitHub environment)
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
gh variable set AZURE_SUBSCRIPTION_ID --body (az account show --query id -o tsv) -R danswett/agent-bridge
gh variable set RELAY_DEPLOY --body true -R danswett/agent-bridge

# The stamp, staging included, production untouched. The first time, staging needs an image.
# ACR Tasks (az acr build) are refused on this free-trial subscription, so push one with the
# SDK and a short-lived registry token (DOCKER_CONFIG pointing at a config.json with
# auth = base64("00000000-0000-0000-0000-000000000000:<az acr login --expose-token>")):
dotnet publish src/AgentBridge.Relay -c Release --os linux --arch x64 -t:PublishContainer -p:ContainerRegistry=agentbridgerelay59731.azurecr.io -p:ContainerRepository=agent-bridge-relay -p:ContainerImageTag=<sha>
./infra/deploy.ps1 -WhatIf -StagingImage agentbridgerelay59731.azurecr.io/agent-bridge-relay:<sha> -StagingVersion <sha>
./infra/deploy.ps1 -StagingImage agentbridgerelay59731.azurecr.io/agent-bridge-relay:<sha> -StagingVersion <sha>
```

`deploy.ps1` seeds staging's vault with fresh keys, and production's with the keys the live
app already uses. It leaves the environment `cae-agent-bridge` exactly as it is until the
cutover (it has the Consumption workload profile and, being hand-made, sends logs nowhere);
`-Production` also attaches the stamp's Log Analytics workspace to it.

## Production cutover

Not done yet. It needs the coordinator's go-ahead. Everything below is reversible.

Before:

1. A `main` build has passed staging (its run's summary shows the image digest and SHA).
2. `./infra/deploy.ps1 -WhatIf -Production -ProductionImage <digest> -ProductionVersion <sha>`
   shows changes to `agent-bridge-relay`, the environment gaining its log destination, and
   the role assignment for the production principal; nothing deleted elsewhere.
3. Note the live revision, the rollback target:
   `az containerapp revision list -g rg-agent-bridge-relay -n agent-bridge-relay --query "[?properties.active].name" -o tsv`,
   and save the live definition as a record:
   `az containerapp show -g rg-agent-bridge-relay -n agent-bridge-relay -o yaml > relay-before-cutover.yaml`
   (outside the repository; it holds no secret values).

Cutover (about a minute; each device reconnects once):

```powershell
./infra/deploy.ps1 -Production -ProductionImage <digest> -ProductionVersion <sha>
dotnet run --project tools/AgentBridge.RelaySmoke -c Release -- --relay https://agent-bridge-relay.agreeablepebble-329880d4.westus2.azurecontainerapps.io --origin https://danswett.github.io --expect-version <sha> --expect-push
```

This keeps the app's name, environment and therefore its URL, keeps its system identity, adds its own identity, the
vault-backed `token-key` and `vapid-key` (the same values), probes, the drain settings and
the `agent-bridge-managed=infra` tag that lets `relay.yml` roll it from then on.

Then check, in this order:

- [ ] `/healthz` shows the new `version`; the smoke test passes.
- [ ] Each host's log shows `connection ended … 1012 restarting` and then `connected as …`
      within seconds, with no `refused`.
- [ ] The PWA on the iPhone, already signed in, lists the machines and opens a session
      without signing in again.
- [ ] A question raises a notification on the locked phone (the VAPID key carried over).
- [ ] Answer and reply from the phone still deliver.
- [ ] Log Analytics shows JSON lines with no login, account id or device name:
      `ContainerAppConsoleLogs_CL | where ContainerAppName_s == 'agent-bridge-relay' | take 50`.

## Rollback

- **A later bad build:** run `relay.yml` with `rollback_to=<previous SHA>` (the SHA is in
  the production job's summary), and approve it.
- **The cutover itself:** make a new revision from the spike's last revision:

  ```powershell
  az containerapp revision copy -g rg-agent-bridge-relay -n agent-bridge-relay --from-revision <the revision noted before the cutover>
  ```

  That restores the spike's whole template: its image, settings, and no probes. An
  image-only rollback would not do: it would keep the readiness probe on `/readyz`, which the
  spike does not serve, so the revision would never become ready and traffic would stay on
  the new one. The app-level parts the cutover changed stay, and the spike works with them:
  `token-key` and `vapid-key` keep their names and values (now read from the vault), and the
  new identity can pull from the same registry. Tokens and push subscriptions stay valid
  throughout. `roll-production.sh` still treats the app as adopted afterwards, so the next
  approved run rolls a product image onto it again.

## Hostnames, Pages and hosts

Production keeps its current URL, so nothing else changes: `pages.yml` bakes it into the PWA's
`config.js` and Content-Security-Policy, and each host stores it in its profile's
`relay.txt`. If production ever moves (a new app name, region or a custom domain such as
`relay.<domain>`, which is the design's long-term name):

1. Add the new name and keep the old one serving the same app (a custom domain on the same
   Container App does that), so existing hosts and installed PWAs keep working.
2. Change `S4_RELAY` in `.github/workflows/pages.yml`; the next Pages deploy updates the
   PWA's relay and CSP. Installed PWAs pick it up on their next open.
3. Hosts: `agent-bridge-host sign-in --relay <new url>` rewrites `relay.txt`.
4. Retire the old name only when no host connects to it.

## Adding a region

Copy `regions/westus2.bicepparam` to `regions/<region>.bicepparam` with new names, create the
resource group, and run `./infra/deploy.ps1 -Region <region> -ResourceGroup <rg>`. Until
home/forward routing exists, an account's devices must all use one region, so a second
region is a separate relay URL, not more capacity behind one name.

## Production behaviour in the code

- **Probes:** startup and liveness on `/healthz`, readiness on `/readyz`.
- **Drain:** on SIGTERM the relay marks itself draining (`/readyz` 503, new sockets 503),
  sends every device 1012, and stops within 20 s, inside the 30 s termination grace period.
- **Limits:** 64 KiB frames (`Wire.MaxFrame`), 4 KiB control messages, 200 frames/s per
  socket, 32 devices per account, 30 pushes/min per account, 60 sign-in calls/min and 120
  new sockets/min per client address, 16 KiB request bodies, 10,000 sockets per replica.
- **Logs:** JSON to stdout, collected by Log Analytics (30 days, 0.1 GB/day cap). No
  account, device, name, address, token or push endpoint: a socket is a random tag that
  means nothing outside the process. ASP.NET request logs are off.
- **Metrics hook:** meter `AgentBridge.Relay` (connections, connects by kind, disconnects
  by outcome, refusals by code, frames and bytes forwarded, undeliverable, pushes and
  sign-ins by outcome), tags limited to `kind`, `outcome` and `code` and held to that by a
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
| **Total** | **≈ $19/month** | **≈ $19–20/month** |

Levers when it matters: GHCR instead of ACR saves the $5 registry; a second region is
another ≈ $14; the next step up is 0.5 vCPU / 1 GiB (≈ $34) for tens of thousands of sockets.
