# APNs registration-binding implementation cuts

Companion to [the contract](apns-registration-binding.md). Every cut starts
with its listed failing tests; estimates include tests and fixtures. Each is
under 250 changed lines. The estimates sum to about 4,670 lines (about 4.7k).
A3 does not edit the A1/A2 branches or authorize provider delivery.

## External gates

- **Host v2 (`G-host`):** `AcceptHostSign` remains off. It is gated on #2366
  verifier-evidence support, #2369 hostSign custody/descriptor, and #2370
  registry signing; #2367 host enrollment/wiring is last. A3 never flips the
  switch or treats a self-declared capability as approval.
- **Person v3 (`G-person`):** #2385 capSign/v3 reader, parser, and verifier cuts
  must reach every roster reader, including relay and offline clients, before
  any v3 writer. No writer upgrades or bypasses a reader.
- A3b1c, A2c, and A2d are gated on the applicable external prerequisites above.
  Both reader families and Chief acceptance of the exact spec head precede A3
  implementation.

| Cut / estimate / owner | First failing tests, then owned change | Depends on |
|---|---|---|
| A1a — `ApnsPush.cs` wire/parser / ~220 / A1 | Duplicate/unknown fields, round-trip, Done silence; own registration ID, proof/receipt, sound, and priority fields. | This spec accepted |
| A3b1a — APNs storage infra / ~190 / A3 | Missing backend/role fails APNs readiness, never memory; own Bicep container, scoped managed-identity role, and config. | This spec accepted |
| A3b1b — Azure state store / ~210 / A3 | ETag conflict/retry, caps, 240-KiB preflight, oversized read fails closed; own per-account CAS store including `rosterFence`. | A3b1a |
| A3b2 — relay proof-key trust / ~210 / A3 | Unknown key/ID and bad pin tests; host pinned to A refuses B's `ready`, and B rejects A's proof. Own signer, rotation, origin/relayId pin, and ready identity. | A3b1b |
| A3b1c — roster-bound enrollment / ~230 / A3 | Wrong `dh`, stale ETag/head, held/quarantined, removed entry, and account-token-only tests; expose authoritative roster ETag/frontier and verify exact active v2/v3 keys. | A3b1b; A3b2; G-host; G-person |
| A3b1d — APNs identity gate / ~190 / A3 | Hello spoof, unenrolled device, non-APNs access tests; gate APNs handlers only and withhold `ready` on incomplete identity. | A3b1c |
| A3c — relay frame parser / ~200 / A3 | Duplicate/unknown properties, types, malformed IDs and bounds; own strict APNs DTO parser. | A3b1d; A1a |
| A3e — authorization records and slots / ~210 / A3 | New ID+token replaces the active slot in one CAS and invalidates its old receipt; target is a distinct slot; no dual-active or cross-slot ID reuse. Own durable receipts and 16/384 caps. | A3b1b; A3b1c; A1a |
| A3d1 — phone authorization / ~200 / A3 | Enrollment, proof claims, cap, expiry, and wrong-host tests; issue five-minute proofs. Do not place receipt replay or register-race tests here. | A3b2; A3c; A3e |
| A3f1 — register and restore / ~210 / A3 | Receipt replay, closed-app reconnect, restore-before-send tests; own durable activation and idempotent register/restore. | A3d1; A3e |
| A3d2 — revoke and generation race / ~210 / A3 | Failing-first active A → authorize B → opt-out revokes B → delayed register B returns `stale-registration`; own cancellation and generation fence. | A3f1; A3e |
| A3f2 — ordered control queue / ~190 / A3 | Received-order and register-versus-close tests; serialize register/revoke/send/unregister per connection. | A3d2 |
| A3f3 — shared connection fence / ~210 / A3 | Cross-process reconnect makes the old connection lose its fence; own ETag increments, replacement teardown, and binding cleanup. | A3f2; A3b1b |
| A3g1 — roster-fenced send authorization / ~220 / A3 | Failing-first: remove a device from the roster but leave APNs blob stale; send returns `roster-stale`, reconciles, and never reaches provider. Repeat after crash between removal and cleanup; test two-process revoke/send CAS orders. | A3f3; A3b1b; A3b1c |
| A3g2 — send policy and capability / ~210 / A3 | Old-relay refusal, invalid sends, and 24-hour clamp; own targets, bounds, silent Done, expiry, and exact capability shape. | A3g1; A1a |
| A3h — RelayClient methods / ~210 / A3 | Unsupported relay identity/capability and restore-before-send tests; own challenge/enroll/authorize/register/restore/send methods and typed results. | A3g2; A3b1c; A3b2; A3f1 |
| A2c — Windows host enrollment / ~190 / A2 owner | Missing key, wrong `dh`, legacy host, and relay mismatch tests; use existing `HostSignKey.OpenExisting`, v2 approval, and refuse signing on pin mismatch. | A3h; A3b1c; G-host |
| A2d1 — Apple/Swift capSign custody / ~100 / A2 owner | Missing/corrupt key and restart tests; add Secure Enclave custody without silent key creation or export. | G-host; G-person |
| A2d2 — Apple/Swift v3 descriptor writer / ~100 / A2 owner | Exact schema, binding-proof, approval, and refusal-order tests; emit v3 only after reader gates. | A2d1; A3b1c; G-host; G-person |
| A2d3 — Apple/Swift v3 code/QR flow / ~120 / A2 owner | Golden transcript/code/QR, replay, and substitution tests; own frozen-descriptor exchange. | A2d2; A3h; A3b1c; G-host; G-person |
| A2a — authorization publication / ~210 / A2 owner | Persistence-before-publish tests; own `RemoteAuthorization.PersonOperations`, `PushStore` token/ID/proof persistence, and subscribe publication. Separate child; A3 does not edit #2747. | A2c; A2d3; A1a |
| A2b — host replacement and restore / ~210 / A2 owner | Closed-phone restore and pending opt-out tests; own receipt persistence and pending/active/revoking states. | A2a |
| A4 — provider transport / ~210 / parent #2437 owner | Injected transport and 410 races; own signing/HTTP2, expiry headers, retries, and CAS invalidation of only the matching slot generation. | A3g2; A1a |
| A5 — host send method / ~210 / parent #2437 owner | Policy, disconnect, and unsupported-capability tests; parent owns `IPushSender`/`PushNotifier`, mapping, and typed refusals. | A2b; A3h; A4; A1a |

The order puts A1a before A2a/A3c/A3g2, durable records before authorization
tests, and register/restore before the delayed-activation race tests.
