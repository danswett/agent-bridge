# Pinning a fleet host's device roster (#3450)

`agent-bridge-host roster-pin set` makes a host trust one account's device roster, rooted in the
genesis fingerprint a **person** compared out of band. It writes
`%LOCALAPPDATA%\AgentBridge\<profile>\roster\roster-pin.json` (account, generation, genesis, heads)
once, owner-only, and never over an existing pin. It refuses inside an agent session, from the app's
pipe, with `AGENT_BRIDGE_TOKEN` set or with piped input (exit 4 or 2); nothing on the relay, Home
Assistant or the pipe can reach it. It never takes the fingerprint from the relay: the relay only
supplies the chain, which must start at the genesis you typed. This is fleet-operator bootstrap only
(owner, Nov 20, 13:21 and 13:24 PT); other people's hosts pin from the app, never by typing.

The **genesis fingerprint** is the first 8 bytes of the roster's genesis hash, as lowercase hex in
four groups of four: `7d0f 3a2e 1c4b 4d5e` ([devices.md](../protocol/devices.md#encodings)).

Running `set` opens a relay connection as this host, so the running host's own relay link
reconnects once. The host reads the pin when it starts: restart it from the app afterwards.

## Part 1: owner, first VM

1. **Get the fingerprint from a source you trust.** Open the device that started your roster and
   read the genesis fingerprint it shows ([device-approval.md](../design/device-approval.md#42-the-code-exchange) §4.2, step 5). **As of this runbook no
   Slipgates client displays it yet**; until one does, stop here. Never use a value from the relay,
   this command, an agent session or another host.
2. **Compare it out of band**: read it aloud or compare it on a second trusted screen, every digit.
3. At the first VM, in a terminal of your own (not an agent session), signed in as the host's user:
   ```
   agent-bridge-host roster-pin set
   ```
   Type the 16 digits when asked (spaces between groups are optional).
4. **Success** prints `{"state":"pinned","account":"…","fingerprint":"…"}`. Check that the
   fingerprint is the one you compared. Then `agent-bridge-host roster-pin show` must print the same.
   Restart the host from the app.
5. **Confirm in writing**: post the fingerprint as an issue comment on #3450. That comment's link
   is the only input Part 2 may use.

Any other `state` is a refusal, and nothing was written: `fingerprint-format`,
`fingerprint-mismatch` (stop: the relay's roster does not start at your genesis), `pin-exists`,
`no-account`, `account-mismatch`, `relay-unreachable`, `no-roster`, `no-genesis`,
`genesis-ambiguous`, `other-account`, `generation-mismatch`, `chain-invalid`, `pin-store` or
`pin-readback`.

## Part 2: SRE, VM2 to VM7

The command refuses agent sessions, so each pin is typed by the SRE operator at a person's terminal
on that VM.

1. Take the fingerprint **only** from the owner's written confirmation (the #3450 comment link).
   Never from the relay, a session, a log or another host's `show` output.
2. One VM at a time: run `agent-bridge-host roster-pin set`, type the confirmed value, and expect
   `pinned` with that same fingerprint. On any refusal, stop and report the `state`; do not retry
   with a different value.
3. Read back with `agent-bridge-host roster-pin show`; record the VM, time (PT) and printed
   fingerprint. Restart the host from the app, then move to the next VM.

## Rollback (either part, per host)

Delete `%LOCALAPPDATA%\AgentBridge\<profile>\roster\roster-pin.json` and restart the host.
Without a pin the fleet-target gate closes for that host (`HostRosterReader.Pinned` gives no
roster); `roster-pin show` then prints `unpinned`. Re-pinning is Part 1 or Part 2 again.
