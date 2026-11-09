# Relay messaging cut-over and rollback

**Alerts:** none yet. The undeliverable and fallback alerts are planned (see [Alerts](#alerts));
until they exist, the watch below is by hand.
**Users see:** a message between two fleet sessions arrives late, arrives twice, goes to the
wrong session, or the sender is told it failed.

This is the fleet's session-to-session messaging moving from Home Assistant (HA) to the relay
(#728, own-agents mode, `agentMessaging.remote` = `own-account`). HA stays the fallback the
whole time. The relay carries encrypted frames between a person's own hosts by device id; it
keeps no queue, so a message to an offline host is refused at once as undeliverable.

## Roll back (1 min, no deploy)

Any one of these puts messaging back on HA. None needs a relay deploy or a production
approval. Host config changes are refused inside an agent session (guard #67), so the
per-machine step is **Owner**, at the machine, and is handed over as a one-liner.

| Scope | Do | Takes effect |
|---|---|---|
| Whole fleet | Set the fleet repo variable `FLEET_RELAY_MSG` to `ha-first` | The messenger reads it at least every 30 s |
| One machine (**Owner**) | `agent-bridge-host config set agentMessaging.remote off` on that machine | The next message; no restart |

Then confirm: send one message each way between two sessions and check it arrives once, by HA.

## Triggers

Roll the affected machine back, or the whole fleet if more than one is affected, when any of
these holds. Whoever sees it flips the fleet variable first and tells the SG Migration Lead at
once.

| Trigger | Measured by |
|---|---|
| More than 1% of messages late (over 30 s) or lost, over a soak window | Messenger send and ack timestamps against message ids |
| Send-to-ack p99 over 250 ms for 10 minutes | The same timestamps; the relay's `relay.forward.duration` is the relay's share |
| Relay unreachable for over 2 minutes | `pwsh ops/tools/relay-probe.ps1 -Target all` |
| Any message delivered to the wrong target, or delivered twice | The host's delivered-id record against the sender's |

A fallback to HA is not a lost message, but it is counted: more than about 2% of messages
falling back in a window fails the window.

## Confirm health

```powershell
pwsh ops/tools/relay-probe.ps1 -Target all
(Invoke-RestMethod https://relay.slipgates.com/healthz).online     # devices online
```

On a build that includes #1219, `healthz` also lists connection counts by site and kind
(`kind` = `host` for the machines). One `host` connection per machine is expected; a machine
whose host is missing there cannot receive over the relay, and its messages go by HA.

## Rules during a soak

- **No relay deploy or restart.** A deploy drops every connection for a few seconds, and the
  soak would count it as a relay-down case. Production relay deploys are the owner's approval
  in any case (AGENTS.md "Relay deploys").
- **One change at a time.** Do not change the messenger and the host build together.
- **Kill switch tested first.** A machine is not switched until the rollback above has been
  shown to work on it.
- **Watch, don't poll fast.** Every 15 minutes at most until the event pump is live.

## Who watches

| What | Who |
|---|---|
| Relay health, connection counts, deploys frozen | SG SRE Lead |
| Fallback count, send results, the switch itself | The messaging builder; the owner decides each machine's switch |
| Lost, late or duplicated messages against message ids | SG Quality Lead |

## Alerts

Planned, not live. They need a monitoring deploy (an owner approval) and are batched with the
next relay production approval; the cut-over does not wait for them.

| Alert | Signal | Fires when |
|---|---|---|
| Undeliverable rate | `relay.frames.undeliverable` | Rising from a baseline near zero while all four machines are online |
| Fallback count | The messenger's fallback counter | More than 2% of messages in an hour |
| Connections | `relay.connections` | Already planned as A-SOCKETS in [alerts.md](../alerts.md) |

## After

Write down in the cut-over issue: the machine, the trigger, the times (Pacific) of the first
bad message and of the rollback, and the message ids. Do not paste a token or key.
