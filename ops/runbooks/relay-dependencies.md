# Relay dependencies: roster, blob store, push, sign-in

**Alerts:** A-ROSTER, A-BLOB, A-PUSH, A-SIGNIN.

Each of these fails on its own while the rest of the relay works: devices still connect and
talk. Don't restart the relay for them unless a step says so.

| Dependency | When it fails, users see |
|---|---|
| Roster store (`roster` container, the app's own storage account) | Offline machines missing from lists. Online presence still works (the roster is in memory, written behind) |
| Blob store (`abblobprodwus2`, container `blobs`) | Attachments and transfers over 256 KiB fail. Smaller ones go in-channel |
| Push services (Apple, Google, Mozilla, Microsoft) | No phone notifications. Everything else works |
| GitHub (device flow) | New sign-ins fail. Signed-in devices are unaffected (tokens are verified locally) |

## Confirm

Query `dependencies` (and `push-outcomes` for push) in
[queries/relay.kql](../queries/relay.kql). The `warnings` query shows the exception type.

**What the logs can't tell you.** Roster and blob lines carry only the exception's type name,
by design. An authorization failure and a Storage 5xx are both `RequestFailedException`. Blob
timeouts aren't logged at all; they show only in the `relay.blobs` metric, once it is exported
(OBS-1, #569). So tell the causes apart from Storage's side, as below.

**`roster copy unreadable, replaced on next write: <Type>`** means a stored roster could not be
parsed. The relay loaded that account as empty, and the next write replaces the copy. Do nothing.
If the line repeats for the same account after a write, the writer is at fault: check what
version of the relay wrote it. The line carries no account or device. The `relay.roster`
`corrupt` counter still counts it.

## Mitigate

1. **Roster or blob: always check the role assignments first.** A missing role looks exactly
   like an outage in the relay's logs.
   ```powershell
   $pid_ = az identity show --subscription $sub -g $rg -n id-agent-bridge-relay --query principalId -o tsv
   az role assignment list --subscription $sub --assignee $pid_ --all --query "[].{role: roleDefinitionName, scope: scope}" -o table
   ```
   The identity needs **Storage Blob Data Contributor** on the `roster` and `blobs` containers,
   and **Storage Blob Delegator** on the blob account (docs/relay-deploy.md "Blob store",
   "Machine roster"). If one is missing, re-apply infra without rolling the image (**Owner**,
   docs/relay-deploy.md). Role assignments can take minutes to apply after that.
2. **Then ask Storage what it answered.** Split the account's transactions by response type:
   ```powershell
   az monitor metrics list --resource <storage account id> --metric Transactions --dimension ResponseType --interval PT5M --offset 2h -o table
   ```
   - `AuthorizationError` or `AuthenticationError`: go back to step 1.
   - `ServerTimeoutError`, `ServerOtherError` or `NetworkError`: Storage is having trouble.
     Check Azure status for Storage in West US 2. There's nothing to do but wait: blobs answer
     "unavailable", and the roster retries on its next write (at most every 30 s per
     account).
   - All `Success`, and the relay still logs failures: the fault is between the relay and
     Storage, for example Entra failing to issue the identity's token. Check Azure status for
     Entra and managed identities.
3. **Push: 410 or 404.** A subscription is gone (a reinstalled PWA, or turned off). Hosts drop
   it; this is normal at a low rate.
4. **Push: 5xx or exceptions from one push host.** That provider is having trouble. Check its
   status page. Nothing to do.
5. **Push: everything 401 or 403.** The VAPID key in the vault doesn't match what browsers
   subscribed with: it was rotated or replaced. See [security-events](security-events.md).
   The `push-outcomes` query keeps the exact status; the `relay.pushes` metric keeps only its
   class.
6. **Sign-in refused at a high rate.** Check GitHub status. If GitHub is fine, check that the
   GitHub App's client id (`Relay__GitHubClientId`) is unchanged.

## Find the cause

For storage, the account's own logs aren't collected (no diagnostic settings at `lean`).
Enable them temporarily if a fault repeats (**Owner**, costs ingestion).
