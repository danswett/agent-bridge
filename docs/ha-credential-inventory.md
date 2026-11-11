# HA credential/state inventory (#526, preparation only)

`agent-bridge-host ha inventory --profile <name>` reports one profile's file presence.
It never opens/decrypts files, creates an identity, contacts HA, inspects running
clients or changes anything. Files: `ha-token.dpapi` (Windows), `ha-token`
(non-Windows), `ha-auth.json` (auth/backoff), `ha-connector.json` (lifecycle/state).
`present` proves neither validity nor usability; `absent` is that named file only.
Directories, links or metadata failures are `unknown` (stderr, exit 1); other entries
still run. Exit 0 proves checks completed, not decommissioning or rollback safety.

Later per-profile owner decisions require protected rollback snapshots/windows and
byte-faithful unknown settings/config retention. No token export or legacy import.
Settings (including renamed `agentTokenEnvVar`), legacy stores, backups, receipts
and environments are not read: inventory/approval remains separate, not presumed absent.

Disabling future use, draining/stopping clients, deleting a local copy, remotely
revoking a token and expiring inherited environments differ. Clients may hold a
token after deletion. Revocation can break other consumers and rollback: retain
auth/backoff while any reader remains. Future retries recheck clients and preserve evidence.

Removal requires [#1949's load proof](https://github.com/danswett/agent-bridge/issues/1949)
and [#1396's gate](https://github.com/danswett/agent-bridge/issues/1396), HA-off rehearsal
and owner sign-off. Rollback is HA back on: retain usable protected credentials.
Generic relay/OAuth, device/TPM, native-agent and non-HA credentials are not targets;
the connector, owner guards and G5/G6/G7 stay.
