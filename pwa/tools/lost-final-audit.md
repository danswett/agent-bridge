# Lost-final loopback reconstruction (#2539)

**Reconstruction, not comparable to 2026-11-13 runs.** The original `audit.mjs`
is lost: this is a new tool, not a byte-for-byte reproduction of its 93-second result.
Obtain Quality and Chief validation GO plus SRE CPU confirmation (mask and run
scope) before any execution, including the focused unit tests. Do not infer GO
from this document. Do not add this opt-in audit to a default suite.

From `pwa`, with committed inputs, execute `node tools/lost-final-audit.mjs` only
in the approved scope. Set `AGENT_BRIDGE_CPU_MASK` to the approved effective
process affinity (Windows hexadecimal mask without `0x`, Linux allowed-CPU list);
the audit reads and checks it, never changes it. Browser children inherit affinity.
Capture stdout as the JSON artifact; any failure exits nonzero, not with a partial
success report. Dependencies are the existing PWA Playwright/esbuild packages.
Focused mechanics tests: `node --test test/unit/lost-final-loopback.test.mjs`.

The harness builds the pinned checkout and serves it on an ephemeral IPv4 loopback
port, blocks non-loopback browser requests, and uses fresh non-persistent Chromium
contexts at 390x844 for baseline then faults. Each phase submits one text reply
through real ReplyService/HostClient to an isolated DemoHost with zero host work delay.
Both phases add 200 ms receive and 200 ms delivery; faults discards the first
correlated final response for each reply UUID, never a retry's response, progress,
or another operation. The unchanged product timers are 90000 ms and 3000 ms.

Differences: revision and script/fixture hashes are recorded, selection is
deterministic by UUID rather than the unknown original selection, and dropping is
a demo transport callback omission, not TCP loss or channel closure. Sampling is
service-submit-to-settled, once per phase, without warmup, clock overrides, UI tap
timing, percentiles or statistical claims. Original timer-binding and strict
listener-closed options cannot be reconstructed exactly. No production relay,
WebKit/iPad, real TCP-loss or energy claims are made. No host/protocol/payload change.
