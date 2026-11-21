# Owner decisions

## Nov 17 22:40 PT: agent fleet-launch grant

Owner decision via Chief, recorded by [H1](https://github.com/danswett/agent-bridge/issues/3053):
a person issues one revocable, expiring grant from the app for the fleet-launcher principal,
`session.launch` only, fleet roles only. The full [Cut H](https://github.com/danswett/agent-bridge/issues/3052)
requires durable attributed launch audit, exact request keying/replay refusal, per-host caps (8/hour, 6 live),
run queue under 8 and `agents.enabled`. H1 stores/manages the grant only; H2/H3 enforce it.
Native agent fleet launches stay off until those slices land and the owner issues the grant.
Answering, replying and trust remain person-only; ending stays behind the existing SRE gate.
