# Security events: unexpected changes, exposed keys

**Alerts:** A-CHANGE (a write to production by someone other than its deploy paths). Also use
this page when a key, token or connection string has been seen anywhere it shouldn't be:
output, a log, an issue, a PR.

This is always at least **Sev 2**, and **Sev 1** if a signing key is exposed. Every step that
changes production is the **owner's**.

## An unexpected change (A-CHANGE)

1. Read the activity log entry: who (principal), what operation, when.
   ```powershell
   az monitor activity-log list --subscription $sub -g $rg --offset 24h --query "[?operationName.value != null && contains(operationName.value, 'write')].{at: eventTimestamp, who: caller, op: operationName.value, status: status.value}" -o table
   ```
2. If it's the owner, or a known `deploy.ps1` run: note it and close.
3. If not: treat the principal as compromised. **Owner:** remove its role assignments,
   disable its federated credentials, re-apply infra from `main`, and roll production back to
   a known digest ([relay-bad-deploy](relay-bad-deploy.md)). Then rotate keys (below) if the
   principal could read the vault.

## An exposed key

| What leaked | Impact | Do |
|---|---|---|
| `token-key` | Anyone can mint account tokens for any account | **Rotate now** (docs/relay-deploy.md "Key rotation", token key). Every device signs in again. The roster starts empty |
| `vapid-key` | Anyone can send pushes to subscribed browsers | Rotate (same section, VAPID). Each PWA re-enables notifications |
| An account token | That account's devices can be impersonated until it expires (about 30 days) | There's no per-token revocation at `lean`. Rotate `token-key` if the account matters, and record the decision |
| A device's private key | That device can be impersonated, by anyone who also has a valid account token for it. "Forget" only removes an offline roster entry: it neither blocks the key from reconnecting nor revokes trust that peers have placed in it | **Owner, at the machine** (guard #67): give the device a **new key**. Signing the existing profile in again doesn't do that: it rewrites only `account.json`, and the profile keeps its key (`Identity.Load`, `Identity.Save`). Instead, set up a **new profile**, which makes a new key, sign it in, and move the host to it. Once that works, retire the old profile folder and its key. On every host that pins the old key as a trusted peer (#402's opt-in pins, `host.peers.revoke` in docs/host-config.md), remove it. There's **no per-device revocation at the relay** at `lean` (device approval, #55, is future work). So if the old key and a token may both be in someone else's hands, rotate `token-key` too: that signs every device out, and only devices that sign in again come back |
| A GitHub OAuth app secret | Token revocation could be abused | Regenerate it in the GitHub App settings and update the vault |

Rotate by the documented procedure only. It never puts key material on a command line, in a
file or in output.

## Afterwards

- Remove the exposed text where possible: edit the comment, or ask GitHub Support to purge it
  for a commit. Rotation is what makes you safe, not removal.
- A review is required ([incidents.md](../incidents.md)). The 2026-10-29 token-key exposure
  is the precedent (docs/relay-deploy.md "History").
