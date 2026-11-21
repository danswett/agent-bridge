# Pinning a fleet host's device roster (#3450)

`agent-bridge-host roster-pin set` makes a host trust one account's device roster, rooted in the
genesis fingerprint a **person** compared out of band. It writes
`%LOCALAPPDATA%\AgentBridge\<profile>\roster\roster-pin.json` once, owner-only, never over a pin.
It refuses in an agent session, from the app's pipe, with `AGENT_BRIDGE_TOKEN` set or piped input
(exit 4 or 2); the relay only supplies the chain, which must start at the genesis you typed. It pins
only a **v3 genesis** whose person descriptor carries capSign (owner, Nov 20, 13:36 PT): until the
Roster library parses v3 (K1, brief 189) every `set` refuses with `genesis-not-v3`, "pin refused:
genesis predates capSign; the account needs a v3 genesis". Fleet-operator bootstrap, the owner-approved bootstrap host only.

## Owner, owner-approved bootstrap host only

1. **Blocked until a Slipgates client shows the genesis fingerprint (briefs 186/187):** the first 8
   bytes of the genesis hash, `7d0f 3a2e 1c4b 4d5e` ([devices.md](../protocol/devices.md#encodings)).
   Compare it out of band, every digit; never take it from the relay, an agent or another host.
2. In a terminal of your own on the owner-approved bootstrap host: `agent-bridge-host roster-pin set`, then type the 16 digits.
3. Success prints `{"state":"pinned",...}`. `roster-pin show` prints one line,
   `UNTRUSTED genesis fingerprint: 7d0f 3a2e 1c4b 4d5e` (this host's own file only): check it
   against what you compared, then restart the host from the app (the relay link reconnects once).

Any other `state` is a refusal and nothing was written: `fingerprint-format`, `fingerprint-mismatch`
(stop: the relay's roster does not start at your genesis), `genesis-not-v3`, `pin-exists`,
`no-account`, `account-mismatch`, `relay-unreachable`, `no-roster`, `no-genesis`,
`genesis-ambiguous`, `other-account`, `generation-mismatch`, `chain-invalid`, `pin-store`, `pin-readback`.

## The other owner-approved fleet hosts

Not typed: each is approved with one tap in the Slipgates app (briefs 184 and 185), as for users
(owner 13:43 PT).

**Rollback (per host):** delete `roster-pin.json` and restart the host; `roster-pin show` then prints `unpinned`.