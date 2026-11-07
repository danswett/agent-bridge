# Certificate and DNS

**Alerts:** A-CERT and A-DNS (the daily `ops-probe.yml` opens an `area:ops` issue), or A-AVAIL
failing while the probe shows the app's own name (`-Url https://agent-bridge-relay.icyflower-4f71155d.westus2.azurecontainerapps.io`) healthy.

`relay.slipgates.com` is served too (same app, own certificate, same DNS-only CNAME and `asuid` rules; see docs/relay-deploy.md). Check both with `relay-probe.ps1 -Target production` and `-Target production-slipgates`.

`relay.bad-duck.com` is a Cloudflare CNAME, **DNS only (grey cloud)**, to the production app,
plus a TXT record `asuid.relay` holding the environment's verification id. The certificate is
Azure-managed: free, six months, renewed by the platform about 30 days ahead, and **only while
both records stay as they are** (docs/relay-deploy.md "Custom domain"). Proxying the record
(orange cloud) breaks renewal and cuts WebSockets.

## Confirm

```powershell
pwsh ops/tools/relay-probe.ps1     # dns cname, dns asuid txt, tls certificate rows
az containerapp env certificate list --subscription $sub -g $rg -n cae-agent-bridge-relay-wus2 --managed-certificates-only -o table
```

| Probe says | Do |
|---|---|
| `dns cname` FAIL "no CNAME" | Someone proxied or deleted the record. **Owner:** in Cloudflare, set `relay` back to a CNAME to `agent-bridge-relay.icyflower-4f71155d.westus2.azurecontainerapps.io`, DNS only |
| `dns asuid txt` FAIL | **Owner:** restore the TXT `asuid.relay` with the value in docs/relay-deploy.md |
| Both PASS, certificate < 21 days | Renewal is late. Check the certificate's provisioning state in the command above. Re-running `./infra/deploy.ps1 -Production -ProductionImage <running digest> -ProductionVersion <sha>` (**Owner**) re-states the binding and nudges issuance |
| Certificate expired | Devices refuse TLS. Fix the records, then re-run `deploy.ps1` as above. As a stopgap, every client can be pointed at the app's own name (docs/relay-deploy.md "Rollback": the move to relay.bad-duck.com), but that's a sign-in on every host: a last resort |

## Afterwards

Find out who changed DNS, and why. Consider a Cloudflare API token, read-only, so the
availability test or a probe can check the records' proxied flag directly.
