# Reconnect storm, refusal spike

**Alerts:** A-STORM, A-RATELIMIT, A-REFUSALS.

A storm is many devices disconnecting and reconnecting over and over. Usual causes:
- a client build looping (connect, fail, reconnect);
- the relay closing sockets it shouldn't;
- a network event;
- abuse.

A deploy causes one wave of reconnects (1012) by design, which is not a storm.

## Confirm

Run these in [queries/relay.kql](../queries/relay.kql):
- `reconnect-storm`: the ratio over the baseline, minute by minute;
- `connections`: which device kind, and which disconnect outcome (`closed`, `dropped`,
  `rate limited`, `frame too large`);
- `session-lengths`: many sockets under 10 s means a client loop.

## Mitigate

| Pattern | Likely cause | Do |
|---|---|---|
| Began with a Windows release; kind `host` or `app`; short sessions | A client bug | Halt the release: [windows-release](windows-release.md) |
| Began with a Pages deploy; kind `pwa` | A PWA bug | [pwa](pwa.md) |
| Began with a relay deploy | A relay bug closing sockets | [relay-bad-deploy](relay-bad-deploy.md) |
| Steady short sessions of one kind with no change of ours; clients log close 4009 (replaced) | Two copies of one device (the same key) taking the slot from each other | A duplicated profile or VM image. Find it from the owner's machine list, not from the relay (it logs no identities) |
| Disconnect outcome `rate limited` (4008) or `frame too large` (4013) rising, no change of ours | An admitted client sending too fast or too much: abuse, or a client bug | The relay is already closing those sockets. Watch CPU and the log cap ([log-cap](log-cap.md)). If it began with a release, halt it. There's no IP blocking at `lean` (no WAF); design §5.8's Front Door WAF is the step up |

**Refusals** (sockets turned away before admission: 4000 bad hello, 4001 token, 4003 proof,
4029 too many devices, 403 origin) **aren't logged by the relay.** They show only in the
`relay.refusals` metric, once it's exported (OBS-1, #569), and in clients' own logs. Until then,
read a refusal spike from a client's log on a machine you own:

| Refusal | Likely cause | Do |
|---|---|---|
| 4001 | Tokens refused: a key rotated, or tokens expired (they last 30 days) | Expected after a rotation. Otherwise check that `token-key` in the vault didn't change ([security-events](security-events.md)) |
| 4000 or 403, with no change of ours | A misconfigured client or a scan | The relay already refuses them. Watch CPU and the log cap |
| 4029 (too many devices) | An account at its 32-device cap: devices never forgotten, or a client making a new device key on every start | Forget the account's stale devices from the owner's machine list. A client minting keys is a bug: halt its release |

Storms also fill the log cap. If A-LOGCAP fires too, see [log-cap](log-cap.md).

## Afterwards

If clients retried in step, DEP-2 (#578, jittered backoff) is the lasting fix.
