# Incident: <symptom, in a few words>

<!-- Title the issue "Incident: <symptom>", label area:ops. Times in Pacific.
     Never paste an account, device id, machine address, token or key: this repository is public. -->

**Severity:** 1 / 2 / 3
**Status:** investigating / mitigated / resolved
**Started:** <yyyy-mm-dd HH:mm PT, first sign>   **Detected:** <time, and how: alert id, a person, a probe>
**Mitigated:** <time>   **Resolved:** <time>

## Impact

Who and what was affected, for how long. For example: "Every device lost the relay for
12 min", or "Hosts on 0.1.1200 didn't start (2 machines)".

## Recent changes

- Relay deploy: <run link and digest, or none in 24 h>
- Windows release: <version, or none>
- Pages deploy: <commit, or none>
- Infra or DNS: <what, or none>

## Timeline (PT)

| Time | What happened or was done | By |
|---|---|---|
| | | |

## Probe output

```
<pwsh ops/tools/relay-probe.ps1 -Target all>
```

## Next steps

- [ ] Mitigate
- [ ] Confirm recovery (probe, alerts clear, devices back online)
- [ ] Review (Sev 1–2: within a week, templates/review.md)
