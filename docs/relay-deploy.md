# Deploying the relay

How the relay is watched once it's deployed (SLOs, alerts, runbooks, incidents, the read-only
probe) is in [ops/](../ops/README.md).

The relay runs on Azure Container Apps (Linux, consumption) as one regional **stamp**
(design §5.8, size `lean`). `infra/` describes the stamp in Bicep; `.github/workflows/relay.yml`
builds an image per commit, rolls it onto **staging**, smoke-tests it there, and offers it to
**production**, which waits for a person to approve.

| | Production | Staging |
|---|---|---|
| Container App | `agent-bridge-relay` | `agent-bridge-relay-staging` |
| URL | **`https://relay.bad-duck.com`** and **`https://relay.slipgates.com`** (also `https://agent-bridge-relay.icyflower-4f71155d.westus2.azurecontainerapps.io`) | `https://agent-bridge-relay-staging.icyflower-4f71155d.westus2.azurecontainerapps.io` |
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
| CORS and origins | `Relay:AllowedOrigins` = `https://danswett.github.io`, and `https://app.slipgates.com` from #393 (both stamps). Staging alone also allows `https://preview.slipgates.com` (`stagingExtraAllowedOrigins`) | Same origins as staging, without the preview one | Set the same origins (they are in `regions/westus2.bicepparam`) |
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
   It also saves the image (`docker save`) as a one-day artifact for scan.
3. **scan** (no environment, no Azure sign-in): Trivy over that saved image. A high or critical
   vulnerability with a fix available fails it, so staging and production never run the image;
   rebuild on the fixed base image or bump the package, or, to accept one for a while, list it
   in `.trivyignore` with a reason and an `exp:` date. A vulnerability with no fix is not
   blocking (ci.yml's `relay-image-sarif` reports those to code scanning from main). It also
   writes the image's CycloneDX SBOM (artifact `relay-image-sbom`). CI's `product` job runs the
   same check on every pull request, so this catches only what was published since.
4. **attest** (`main` only, beside staging): SLSA build provenance and the SBOM, bound to the
   pushed digest, in GitHub's attestation store (nothing is pushed to the registry). To check
   the image a revision runs, after `az acr login -n abrelaywus2`:
   `gh attestation verify oci://abrelaywus2.azurecr.io/agent-bridge-relay@sha256:<digest> -R danswett/agent-bridge`
   (add `--predicate-type https://cyclonedx.org/bom` for the SBOM).
5. **staging** (after build and scan): `az containerapp update` to that digest with `Relay__Version=<sha>`, then
   `relay-smoke` against staging with staging's token key: health at the new version,
   readiness, no app code, the push key, CORS and origins, 4001, and two devices of a
   throwaway account that find each other in presence, open a Noise channel, echo a
   message, get "undeliverable" for an absent device, have an off-allowlist push refused,
   and leave presence; and (`--expect-blobs`) a 5 MiB file sealed, uploaded to the blob
   store, refused to another device of the account and to another account, fetched,
   opened and checked by its recipient, deleted, and then not found; then a 10 MiB file
   streamed from disk as a host sends a session bundle ([host-transfer.md](host-transfer.md)),
   fetched, checked and deleted the same way. The two-device step also checks the machine
   roster ([protocol/roster.md](protocol/roster.md)): the host that left is listed as offline in
   the app's presence, and the app (kind `app`, a person's device) forgets it.
6. **production** (`main` only, environment `production`): waits for approval by the
   required reviewer. `infra/roll-production.sh` refuses an app `infra/` has not adopted,
   writes the image it replaces to the job summary, rolls the digest, and the smoke test
   runs again without a token.
7. **rollback**: run the workflow by hand with `rollback_to=<image digest>` (`sha256:…`); see
   [Rollback](#rollback). Same approval, same script, same smoke test. Not scanned again: it
   returns to an image that ran.

Every job that touches Azure (build, staging, production, rollback) signs in with GitHub OIDC
(`azure/login`); only those jobs, and `attest`, get `id-token: write`, and `infra`, which only
compiles the templates, cannot mint a token. `attest`'s token signs the attestations through
Sigstore and nothing else: it runs without an environment, and each principal below trusts only
its environment's subject. Trivy runs in `scan`, which has no token beyond `contents: read`,
never in `build`, which can sign in as staging. There is no
client secret anywhere. Each environment has its own principal:

| Principal | Roles |
|---|---|
| `agent-bridge-relay-deploy-staging` | AcrPush on the registry; Contributor on the staging app; Managed Identity Operator on its identity; Key Vault Secrets User on the **staging** vault; Reader on the resource group |
| `agent-bridge-relay-deploy-production` | Contributor on the production app; Managed Identity Operator on its identity; Reader on the resource group |

Each has one federated credential for its environment. GitHub now presents subjects with the owner's and repository's numeric ids, `repo:danswett@3709749/agent-bridge@1436251473:environment:<name>`, so a renamed or recreated repository of the same name cannot sign in; the plain `repo:danswett/agent-bridge:…` form is refused (AADSTS700213). The
`production` environment allows only `main` and requires `danswett`'s approval. Repository
variables `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID` (the stamp's subscription), and each environment's
`AZURE_CLIENT_ID`, are ids, not secrets. The repository variable `RELAY_DEPLOY=true` turns
the Azure jobs on; without it (a fresh clone) the workflow only compiles `infra/`. A fork's
pull request never reaches Azure: GitHub gives it a read-only token, no OIDC token and no
secrets. Dependabot's pull requests skip the Azure jobs too (`build`'s condition), because their
branches are in this repository: a bumped action reaches staging's sign-in only once a person
has reviewed and merged it. That condition, which also skips any `dependabot/` ref whoever runs
it, not the `staging` environment, which has no branch policy or reviewer, is what keeps
Dependabot's branches out.

### Proposed, pending the owner's decision (#418)

Rollback by digest stops a re-pushed tag reaching production. Two further changes would narrow
what staging can do at all; neither is applied, and both change live Azure or GitHub settings,
so they wait for the owner:

1. **Staging's registry write.** Today `agent-bridge-relay-deploy-staging` holds AcrPush on the
   whole of `abrelaywus2`, the registry production pulls from. Options, most contained first:
   a separate staging registry (staging pushes there; production's image is imported into
   `abrelaywus2` by digest, `az acr import`, when the production job runs, which needs import
   rights on `abrelaywus2` for the production principal instead); or a repository-scoped ACR token in place of the role
   (a Basic registry has tokens and scope maps); or ACR tag locking
   (`az acr repository update --write-enabled false` on each tag after build pushes it, which
   needs a role beyond AcrPush). Production by digest makes the third the least valuable.
2. **The `staging` environment.** A required reviewer, or a deployment-branch policy (`main`
   only, which would also stop pull requests deploying to staging before merge), so that an
   unreviewed same-repository branch cannot sign in as staging at all. Today `build`'s
   condition, and GitHub withholding OIDC from forks, are what keep others out.

Infra changes (probes, sizes, secrets, a new region) are not applied by CI. A person runs
`infra/deploy.ps1`, which never rolls an image: it re-states what each app runs now.
A pull request that changes `infra/` is also checked by PSRule for Azure (`iac-scan.yml`,
configured by `infra/ps-rule.yaml`) over each region's `.bicepparam`, production's resources
included. It is advisory: its findings are warnings and a table in the run's summary, and those
already on main when it was added, with their fixes, are listed in #441.

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

### Second name: relay.slipgates.com (#540)

Production answers on **both** `relay.bad-duck.com` and `relay.slipgates.com` (`extraCustomDomains` in the
region's parameter file). Both are served directly by the same app, each with its own managed
certificate: a WebSocket upgrade does not follow a redirect, so old clients (every paired host, the app
and PWA already installed) keep working with no re-pairing. Do not put a redirect on either name.
The relay's Content-Security-Policy follows the request's Host, so it needs no change. DNS for
`relay.slipgates.com` (DNS-only CNAME to the app, `asuid.relay` TXT) is done; `slipgates.com` has no CAA records.

Rollout, staging first, production on the owner's approval, by someone with Azure access:

1. `./infra/deploy.ps1 -WhatIf -Production -ProductionImage <running digest> -ProductionVersion <running sha>` and read the diff (production is only included with `-Production`): only the new hostname is added;
   `relay.bad-duck.com`'s binding is unchanged.
2. `./infra/deploy.ps1 -Production -ProductionImage <running digest> -ProductionVersion <running sha>`. Pass 2
   puts the hostname on the app, pass 3 issues and binds the certificate (a few minutes). Each hostname steps
   on its own, so one still waiting for DNS never unbinds the other.
3. Check both names: `pwsh ops/tools/relay-probe.ps1 -Target production` and `-Target production-slipgates`.
   For the WebSocket and blob round trip, run `tools/AgentBridge.RelaySmoke` with the token key against each name
   (`--relay https://relay.bad-duck.com` then `--relay https://relay.slipgates.com`, `--expect-blobs`).
4. Set the repository variable `RELAY_SLIPGATES_LIVE` to `true` so every later promotion smoke-tests the new name too.

Rollback: first set `RELAY_SLIPGATES_LIVE` to `false` (otherwise every promotion smoke-tests a name that is gone and fails), then remove `extraCustomDomains` from `production` and re-run step 2. The hostname comes off the app
(an incremental deploy leaves its certificate behind, harmless) and `relay.bad-duck.com` is untouched; client defaults now point at `relay.slipgates.com` (#1112), so revert that change first (`S4_RELAY` in `pages.yml`, the app's default relay); apps with a saved relay keep it. Leave the
DNS records in place (they are harmless unbound). To delete the leftover certificate:
`az containerapp env certificate delete -n <environment> -g <resource group> --certificate mc-relay-slipgates-com`.

New pairings and defaults (`S4_RELAY` in `pages.yml`, the app's default relay) moved to `relay.slipgates.com` in #1112, and the Apple app's default (`BridgeStore.swift`) with it, with both names live. Retiring `relay.bad-duck.com` is the owner's later decision.

The first name's steps work as follows. `deploy.ps1` steps it forward
as DNS allows: it prints both records while they are missing; once they resolve it puts the
hostname on the app (pass 2), then issues the managed certificate and binds it with SNI
(pass 3); after that it re-states the binding. Staging is served at
`https://relay-staging.slipgates.com` (its `customDomain`; DNS-only CNAME `relay-staging` and TXT
`asuid.relay-staging`), so the new-hostname steps can be exercised there before production.

**Renewal** is automatic as long as the CNAME keeps pointing at the app and stays DNS only.
Moving the record, proxying it, or deleting the TXT makes renewal fail silently until the
certificate expires (the current one: 2027-04-29). Check with
`az containerapp env certificate list --subscription 0e38f344-908e-4ff3-88ac-4d499a1f51f8 -g rg-agent-bridge-relay -n cae-agent-bridge-relay-wus2 --managed-certificates-only -o table`.
`ops-probe.yml` checks both records and the certificate's remaining life once a day, and opens
an issue if they drift ([Monitoring](#monitoring)).

## Monitoring

Production's alerts, its availability test and the resource group's budget are in
`infra/modules/monitoring.bicep`. Each alert's meaning and runbook are in
[ops/alerts.md](../ops/alerts.md). Everything goes by email to one action group,
`ag-agent-bridge-ops`, and nothing pages at night (owner, #568).

| Resource | What it watches |
|---|---|
| `ag-agent-bridge-ops` | The action group: one email receiver. Its address is never in the repository |
| `appi-agent-bridge-relay-wus2` | Application Insights, workspace-based (in the stamp's Log Analytics) |
| `wt-relay-healthz` | `https://relay.bad-duck.com/healthz` every 10 min, from West US, East US and West Europe. It must answer `"ok":true` with a valid certificate. Alert A-AVAIL fires when 2 of 3 locations fail within 15 min |
| Metric alerts on `agent-bridge-relay` | Replicas (A-REPLICAS), restarts (A-RESTART, A-CRASHLOOP), memory (A-MEM), CPU (A-CPU), 5xx count (A-5XX) |
| `budget-agent-bridge-relay` | $40 a month on the whole resource group. Emails at 50%, 80% and 100% actual, and 100% forecast |
| `ops-probe.yml` (GitHub, daily) | The certificate's remaining life (A-CERT), the CNAME and `asuid` TXT (A-DNS), health, CORS and the push key. Problems go on one `area:ops` issue, which closes itself when clean (not on a run whose DNS lookups couldn't complete) |
| `logic-agent-bridge-github-watch` | Every 5 min, from Azure: GitHub's status page for Actions and Pages, and this repository's runs queued over 30 min. Records the availability result `github-actions`; alert A-GHA fires when 2 of the last 3 checks found a problem, and A-GHA-SILENT when no fully read result has arrived in 30 min (#658) |

It's applied with production, like any infra change (a person, not CI). The first time, pass the
address alerts go to; later runs reuse the one in the action group:

```powershell
./infra/deploy.ps1 -WhatIf -Production -ProductionImage <running digest> -ProductionVersion <sha> -AlertEmail <address>
./infra/deploy.ps1 -Production -ProductionImage <running digest> -ProductionVersion <sha> -AlertEmail <address>
```

Without an address, `deploy.ps1 -Production` says monitoring was skipped and changes nothing
else. A `-WhatIf` preview is built so it can't print the address. What-if shows each resource's
predicted properties, and a changed property's old value, and Azure doesn't mask an email
address. The action group and the budget each keep a copy. So:
- if both lookups succeed and find neither resource, the preview uses the placeholder
  `alerts@example.invalid`;
- if both exist and already hold exactly what the template writes (the action group's single
  receiver, and each of the budget's four notifications with that one contact, case and all),
  the preview uses it, and no address property shows as changed;
- otherwise (a new or differing address, an extra or missing copy, one resource missing, or a
  failed budget lookup), monitoring is left out of the preview, with a message, and the real
  run applies it. A failed action group lookup stops the script.

The budget's start date can't change once the budget exists, and must be in the current month
when it's created, so `deploy.ps1` passes the existing budget's date, or the first of this month. If it can't list budgets, a real production run stops rather than guess.

Cost: about $6.50 a month for the availability test ($0.0005 a run, 3 locations every 10 min),
and about $2.50 for the GitHub watch (some 90,000 built-in Logic App actions, and its
A-GHA-SILENT log alert);
alert rules and the action group's emails cost cents. Availability results go into the
workspace, a few MB a day against its 0.1 GB daily cap.

## Reading a disconnect

### The disconnect log line and `host-probe` (#970)

Every socket's end is logged as `disconnect <conn> (<end>) after <n>s; frames in .. out .., bytes in .. out ..; cause exception=<type> wserror=<WebSocketError> close=<status|none> keepalive=<timeout|no>`.
`close=none` means no close frame came (a reset or a dead peer); `keepalive=timeout` means the
runtime's ping/pong timeout ended it. The runtime words that only in its exception message, so
`keepalive` is matched on that text. The cause carries no identity, account id, token or message text.

To produce each kind of end against staging, with the token key in `RELAY_SMOKE_TOKEN_KEY` (never echoed):
`relay-smoke host-probe --relay <url> --mode kill|blackhole|hold --hold <s> [--count N]` prints its
throwaway account id and device ids with UTC times; it needs https and refuses the production relay hosts unless `--allow-production` is passed; `--mode observe --account <id> --hold <s>`
prints the roster and presence changes an app of that account sees.

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
and each PWA needs Sign in again. Push subscriptions are unaffected. Each account's machine
roster is stored under a name derived from the token key, so every roster starts again empty:
hosts are listed again as they reconnect, and the old blobs age out under the lifecycle rule
(or delete the `roster` container's contents).

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

- **A bad build:** run `relay.yml` (Actions, *Relay deploy*, *Run workflow* on `main`) with
  `rollback_to=<digest>`, the `sha256:…` the production job's summary gives under "roll back"
  for the image it replaced, and approve the `production` environment as for any deploy.
  `infra/rollback-target.sh` accepts only a digest that a revision of the production app has
  run, and takes the commit that revision ran as the version the smoke test expects; anything
  else is refused before production is touched. To see what production has run:
  `az containerapp revision list -g rg-agent-bridge-relay -n agent-bridge-relay --all --query "[].{created: properties.createdTime, image: properties.template.containers[0].image, version: properties.template.containers[0].env[?name=='Relay__Version'].value | [0]}" -o table`.

  Why a digest (#418): the registry is shared and the staging principal holds AcrPush on it, so
  a tag such as `agent-bridge-relay:<old sha>` can be pushed again by anything that runs as
  staging, a same-repository pull request's build included. Rolling back by a commit's tag
  would then carry that image to production while the approver saw only a familiar SHA. A
  digest is the image's bytes, and only production's own approved jobs create production
  revisions, so neither a re-pushed tag nor an image only staging pushed qualifies.

  Limits: Container Apps keeps up to 100 inactive revisions, so only images production ran
  within its last 100 revisions qualify; and revisions deployed by tag (before production
  deployed by digest, or by an earlier tag rollback) carry no digest to match. To return to
  anything else, deploy it as a new build through staging, or have a person run
  `./infra/deploy.ps1 -Production -ProductionImage <registry>/agent-bridge-relay@sha256:<digest> -ProductionVersion <sha>`
  after checking the digest with `gh attestation verify` (pipeline step 4).
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
* A second private container, `feedback` (docs/protocol/feedback.md): the relay takes each
  beta-feedback bundle itself (exact ticketed size, 8 MiB cap) and writes it with
  create-only semantics. Its role assignment there is Blob Data Contributor with a condition
  that removes read, list and delete, and a 30-day time-based retention policy on the
  container means the relay cannot overwrite or delete a bundle before the
  30 days are up. A lifecycle rule deletes `feedback/` at 31 days; the `blobs` rule is
  scoped to its own container. Reading feedback is a role given to the feedback lead's
  identity (SRE), never to the relay. Environment: `Relay__Feedback__Backend`,
  `AccountUrl`, `Container`, `ManagedIdentityClientId`; empty `Backend` turns it off.
* Feedback reader (staging, `feedbackReaderIdentityName`): a separate user-assigned identity,
  `id-agent-bridge-feedback-reader-staging`, with the built-in Storage Blob Data Reader role
  scoped to the `feedback` container only: no condition, no write or delete, no access to
  `blobs`, no account key or SAS (shared key access is off). It is neither the relay nor the
  writer.
  * **Token without a stored secret.** A user-assigned identity can only get a token from
    inside Azure: attach it to the Azure VM that runs the Feedback Inbox
    (`az vm identity assign -g <rg> -n <vm> --identities <identity resource id>`), then the
    inbox asks the VM's metadata endpoint for a storage token
    (`DefaultAzureCredential` or `ManagedIdentityCredential` with the identity's client id).
    Nothing is stored. Any process on that VM can ask for the token, so the VM is dedicated
    to the inbox and the decryption key stays with the `fbinbox` account (#740). A host that
    is not an Azure VM has no secret-free path for this identity: do not hand it a client
    secret or SAS; ask for a different design.
  * **Cleanup, by name.** Detach it from any VM first (`az vm identity remove ...`), then
    `az role assignment delete --assignee <principalId> --scope <feedback container id>`
    and `az identity delete -g rg-agent-bridge-relay -n id-agent-bridge-feedback-reader-staging`.
    Removing `feedbackReaderIdentityName` from the staging config stops a later apply from
    recreating it.
* The retention policy deploys **unlocked**. Until it is locked, someone with write access
  to the storage account (not the relay) could shorten or remove it, so "no one can
  overwrite or delete" holds against the relay and any data-plane identity, not against an
  account owner. After the first production apply, a person locks it (this cannot be
  undone, and a locked policy can only be extended):

  ```powershell
  $etag = az storage container immutability-policy show --account-name <account> --container-name feedback --query etag -o tsv
  az storage container immutability-policy lock --account-name <account> --container-name feedback --if-match $etag
  ```

  Until then the accepted risk is account-owner tampering; record the lock on #731.

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

## Machine roster

Each account's hosts, online or not, so the PWA and the app can list a machine that is offline
with when it was last seen, and a person can forget one that is gone (#50,
[protocol/roster.md](protocol/roster.md)). The relay holds it in memory and writes it behind,
at most once every 30 seconds per account, and everything left when it stops: after the drain,
within the 20-second shutdown timeout (10 seconds for the roster).

`infra/modules/roster-store.bicep`, one per app (`deployRosterStore`), next to it:

* A StorageV2 account of its own, **Standard_LRS, hot**, HTTPS and TLS 1.2 only, **shared keys
  off**, no public blob access, no CORS (no browser reaches it), no soft delete or versioning.
  Not the blob store's account: its lifecycle rule deletes every blob a day after it is written.
* One private container, `roster`, holding one small JSON blob per account, named by an HMAC of
  the account id under a key from the token key. A **lifecycle rule** deletes a blob not
  modified for 180 days: an online host's roster is rewritten every 10 minutes, so only an
  account none of whose hosts has connected for half a year loses its list (and throwaway smoke
  accounts go).
* For the app's user-assigned identity: **Storage Blob Data Contributor on the container only**.
  No delegator role (nothing is signed), and nothing on the account or any other container.
* Environment for the app: `Relay__Roster__Backend=azure`, `Relay__Roster__AccountUrl`,
  `Relay__Roster__Container`, `Relay__Roster__ManagedIdentityClientId`. No key or secret.
  Without them the relay keeps the roster in memory, so a restart forgets every offline machine
  until each reconnects; `Relay__Roster__Backend=off` turns it off (no `known` in presence).

Names: `abrosterstgwus2` (staging), `abrosterprodwus2` (production). The change is additive:
nothing existing is modified, and an older client ignores `known` and never sends `forget`.

### Deploying it

Production needs a person, as any infra change does; the steps after the merge:

1. **Staging infra:** `./infra/deploy.ps1 -WhatIf`, check it creates only `abrosterstgwus2`, its
   container, lifecycle rule and the one role assignment, and adds the four `Relay__Roster__*`
   variables to the staging app; then `./infra/deploy.ps1`. A taken storage account name shows
   in the what-if; pick another in `regions/westus2.bicepparam`.
2. **Staging image:** `relay.yml` rolls the merge commit to staging and runs the smoke test,
   which now checks the roster. The role assignment can take a few minutes to apply; until then
   writes fail (`relay.roster` outcome `error`, logged by type only) and the roster is held in
   memory and written once they succeed.
3. **Production infra** (approved): `./infra/deploy.ps1 -WhatIf -Production -ProductionImage <running digest> -ProductionVersion <sha>`,
   then the same without `-WhatIf`. It re-states the image already running, so this step only
   adds the account, the role and the variables (a new revision of the same image).
4. **Production image** (approved): the `relay.yml` production job, as for any build.

Until step 3, production runs the new image with the roster in memory, which is correct, only
forgetful across restarts.

### Host-gone state (#970, proposed; not built)

What #970 adds beside the roster ([protocol/host-gone.md](protocol/host-gone.md#privacy-and-retention)),
in #805's lifecycle store, never in this roster blob's v1 schema:

* **Per host, only the current state, overwritten in place:** its arm (the newest
  `host-gone-arm`, with its sealed notices, and the time the relay received it), the latest
  witness state with its receipt time and the app device the host
  declared, the current episode's start, the host's latest departure and reconnect times, the episode's sent targets (handles), `lastSeen` and `gone`. No
  history and no series: a new value replaces the old one.
* **Expiry:** an arm and its notices expire at the notices' TTL (21600 s, 6 h) after receipt; notices are
  deleted once sent; a state witness when its app's connection leaves or 150 s after its receipt
  (`stopped` and `updating` by the 150 s only), and every witness when the host
  reconnects; sent targets (routing handles, at most 8) are kept for the episode, until the host confirms its
  return notice went or the machine is forgotten, **not** on the arm's 6 h clock, so an overnight outage
  keeps them; the
  app binding when that app leaves the roster. `gone` lasts until the host returns or is
  forgotten. The alert caps keep a count per window (at most 24 h), in #805's lifecycle store.
* **Deleted** on forget, on sign-out and when the account changes, with the machine's roster
  entry.
* **Never in logs or metric labels:** no arm, witness, target handle, push body or episode time.
  Metrics carry fixed categories only.

### Rolling it back

* **The image:** `relay.yml` with `rollback_to=<previous digest>`, as for any build. An older relay
  sends no `known` and ignores `forget`; clients then list only what is online and offer no
  Forget.
* **The infra:** nothing has to be undone. Left in place, the account is unused by an older image
  (it ignores the `Relay__Roster__*` variables). To remove it: delete the `Relay__Roster__*`
  variables from the app (a new revision), the role assignment, and the storage account.

## Production behaviour in the code

- **Probes:** startup and liveness on `/healthz`, readiness on `/readyz`.
- **Drain:** on SIGTERM the relay marks itself draining (`/readyz` 503, new sockets 503),
  sends every device 1012, and stops within 20 s, inside the 30 s termination grace period.
- **Reconnects:** every client (host, Windows app, PWA) waits a uniformly random time
  between 0 and min(30 s, 1 s · 2^n) before retry n, so devices closed together don't retry
  together (#578). After a 1012 the first retry is spread over 0-5 s; a close reason of
  `restarting;spread=N` widens that to N seconds (clamped to 1-60) without a client release.
  The relay sends plain `restarting` today. `ReconnectBackoff.cs` and `pwa/js/backoff.js`.
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
| Monitoring ([above](#monitoring)): one availability test, the GitHub watch, alert rules, emails | ≈ $9 | ≈ $9 |
| **Total** | **≈ $28/month** | **≈ $29–32/month** |

Levers when it matters: GHCR instead of ACR saves the $5 registry; a second region is
another ≈ $14; the next step up is 0.5 vCPU / 1 GiB (≈ $34) for tens of thousands of sockets.

## Metrics export (#1134)

The relay's meter (`AgentBridge.Relay`) is exported to Application Insights by the Azure Monitor OpenTelemetry exporter (`RelayTelemetry.cs`), only when `Relay__ApplicationInsightsConnectionString` or `APPLICATIONINSIGHTS_CONNECTION_STRING` is set as an app setting (a connection string, supplied by the deployment, never kept in the repo). Unset, nothing is exported. Instruments added: `relay.forward.latency` (ms, frame received to written to its target), `relay.connect.duration` (ms, socket opened to ready) and `relay.sockets.live` (gauge). Each carries the `kind` tag; `relay.sockets.live` and `relay.connects` also carry `site`, the hostname the device connected through, reduced to a configured name (`Relay__KnownHostnames__0..`; unset means `relay.slipgates.com` and `relay.bad-duck.com`, and setting any replaces both) or `other`, so a client cannot mint values. `/healthz` reports the same counts as `sockets: [{site, kind, count}]` (aggregate only), with a 0 for every configured hostname and kind, so an emptied hostname reads 0 rather than disappearing. To retire a hostname, watch `customMetrics` `relay.sockets.live` by `site` until it stays 0 for 24 h. `RelayMetrics.AllowedTags` is the whole tag allow-list and a test fails if an id-like key joins it. Wiring the app setting in `infra/` is a follow-up that needs the owner's deploy approval.

A malformed connection string does not stop the relay: it logs a generic warning (never the string) and runs without export.
