# S4 native iOS push: reconcile the APNs path

**Proposed design, not implementation or rollout approval.**
[#2398](https://github.com/danswett/agent-bridge/issues/2398), parent
[#1396](https://github.com/danswett/agent-bridge/issues/1396), November 12, 2026 PT.
This first deliverable is documentation only. Engineering must accept the path
before its failing-first implementation slices start. It does not reopen the
Apple distribution decision or mark S4 rows 16/28 passed.

## What exists, and what does not

[Apple parity](../apple-parity.md) explicitly leaves APNs registration, provider,
entitlements and background lifecycle missing. `BridgeNotificationManager` makes
local notices from an open subscription; on iOS it refuses background delivery,
and `AgentBridgeApp` suspends its connections on backgrounding.
`apple/project.yml` has neither a push entitlement nor a service-extension target.

[Design section 8](../design.md#8-phone-pwa-now-native-later) already chooses
the relay push gateway, encrypted native payloads and a notification service
extension. It does not define the native registration or provider contract.
The [S4 matrix PR](https://github.com/danswett/agent-bridge/pull/2409) identifies
this gap. [PWA PR 2416](https://github.com/danswett/agent-bridge/pull/2416) preserves
an early worker tap until page identity is ready; it implements no native APNs path.
Neither historical Web Push nor an Apple foreground fixture proves native delivery.

## One proposed path

An opted-in native app registers with APNs, enrolls with each selected host over
its authenticated encrypted channel, and receives host-sealed notices through
the existing relay gateway's APNs adapter. A service extension decrypts locally.
There is no background WebSocket, HA fallback, content-free polling loop or
silent-push dependency. APNs alert delivery is best effort, not a durable event log.
No new notification policy or remote answer/stop authority is introduced.

### Registration and custody

Use an additive `push.apns` capability with proposed `push.apns.subscribe`,
`push.apns.unsubscribe` and `push.apns.list` operations. Existing browser `push.*`
shapes, VAPID handling and limits remain unchanged; an old host is unsupported,
not silently treated as registered.

Subscription data contains the OS token, a configured native target identifier,
an opaque key identifier and RFC 8291 public key/auth fields. Account and device
identity come only from the secure channel. No caller-supplied account, device
kind, URL, APNs topic or provider credential grants authority.
The target identifies a deployment-controlled bundle/environment mapping; only
fixed sandbox/production APNs origins are allowed, never arbitrary endpoints.
Wire fields and bounds must be pinned in A1's parser tests before runtime use.

Apply the current own-account and person-device policy before enrollment/list/
removal and again before each send, including retries. Preserve current roster
enforcement modes; this note neither enables enforcement nor bypasses it.
Agents, service credentials and the local pipe gain no native subscription rights.
Keep one registration per account/device/target, replacing atomically. A delayed
failure for an old registration cannot remove its replacement.
List results expose service/name/current/since, never token, keys or endpoint.

Generate a distinct encryption key pair and auth secret per account/host binding,
separate from identity/signing keys. Share them only between the app and extension
through a provisioned Keychain access group, with non-synchronizing,
device-only after-first-unlock protection. Before first unlock, decryption is
unavailable; show only the generic fallback. A random key identifier selects the
binding, whose expected host/account/generation is checked after decryption.
Persist consent and generation with the binding, not token-bearing debug data.
The OS token itself stays in app memory: request it anew at each eligible launch,
never restore a cached token. Hosts retain protected subscriptions for closed apps.

### Provider and encrypted payload

The host retains its existing question/finished/resolved decisions, quiet/privacy
settings, device-in-use suppression, delays, collapse window and retry budget.
Reuse `PushMessage` and RFC 8291 encryption; native encoding needs a lower budget:
the existing 3072-byte plaintext ceiling exceeds APNs' 4096-byte JSON limit once
ciphertext is base64 encoded. Initially fit native plaintext to 2048 bytes, then
measure the complete serialized APNs JSON and refuse anything over 4096 bytes.
Never truncate ciphertext, identity fields or a signed/encrypted envelope.

The outer JSON contains only generic alert words, `mutable-content: 1`, the
opaque key identifier and encrypted record. Session/question identities, titles,
response previews and navigation remain encrypted. The gateway may observe the
target, token, size, timing, collapse identifier and delivery priority, not content.
Map the host's topic to APNs collapse identity; choose sound/priority from existing
host policy, with Done always silent. Do not equate APNs coalescing with display.

Use HTTP/2/TLS and token-based ES256 provider authentication in the relay only.
Deployment supplies the APNs signing key; it never reaches a host, app, fixture,
repository, request log or error message. Cache/refresh provider JWTs within Apple's
validity window rather than minting one per notice. Missing configuration refuses
explicitly and leaves Web Push operating; there is no production fallback.
Disable request-URI/header/body logging: APNs puts the device token in its path.

Return typed accepted/transient/invalid-registration/configuration outcomes.
APNs 200 means **provider accepted**, never displayed. Bound 429/5xx/network
retries by existing expiry and attempt limits, rechecking account/grant/generation.
410 invalidates only the matching registration; use its timestamp and registration
identity so an older failure cannot delete a renewal. Other errors retain a safe
reason code; authentication/topic errors are not success or automatic token deletion.

### Extension, activation and cancellation

The extension checks consent/generation, decrypts with the binding's key, validates
the payload and host identity, and respects current preview preferences. Malformed,
revoked, wrong-account, unavailable-key and time-expired work exposes no content.
Always finish within the OS callback budget; the original fallback is generic,
has no actions or private route, and opens only the app. Do not assume an extension
can prevent the OS showing that fallback after timeout.

An unlocked foreground activation may open the exact encrypted host/session/
question only after the active account and fresh host state agree. Startup retains
at most one generation-bound pending tap; sign-out/account changes discard it.
Stale, answered and offline taps never submit, and no APNs callback answers an
approval or stops anything. Deduplicate foreground local and remote presentation.

Disable, sign-out or account change first invalidates local consent/keys/pending
taps and removes this app's notices; unsubscribe selected hosts when reachable.
Report offline cleanup as pending, not completed, and retry removal only under the
original account/binding. Never re-enroll it after an account switch.
Revocation prevents further authorized host sends; already accepted OS notifications
cannot be recalled reliably. Resolved/withdrawal and out-of-order delivery need
device proof; a collapse header alone does not remove a displayed question.

## Proposed remaining issues, not additional work in this slice

Each estimate includes focused failing-first tests and stays at most 250 changed
lines; split before exceeding it. All are sensitive and need both reader families.
A1 is the proposed first code slice, not permission to implement the whole path.

| Key / proposed title | Scope | Lines | Depends on |
|---|---|---:|---|
| A1 Native push contract | Capability, strict destination parsing and cross-client refusal vectors; no sender. | 180-220 | Accepted design |
| A2 Host native registration | Account/person checks, protected persistence, replacement and token-free list/removal. | 200-250 | A1 |
| A3 APNs gateway boundary | Fixed target allowlist, native frame validation and full JSON byte budget. | 180-220 | A1 |
| A4 APNs provider transport | Injected signer, HTTP/2 send, JWT lifetime and typed provider errors; no real credentials. | 200-250 | A3 |
| A5 Host native dispatch | Existing notifier policy to native adapter, retries and generation-safe invalidation. | 200-250 | A2, A4 |
| A6 iOS OS registration | Opt-in authorization, app-delegate token/failure callbacks and visible registration state. | 180-220 | A1 |
| A7 Native push key custody | App/extension Keychain binding, privacy/consent generation and deletion. | 180-220 | Accepted design |
| A8 Swift encrypted notice codec | Bounded RFC 8291 decrypt and payload/host binding checks with generated test material. | 200-250 | A1, A7 |
| A9 iOS extension delivery | Service-extension target/entitlements, generic timeout behavior and host enrollment wiring. | 200-250 | A2, A6, A8 |
| A10 Native push activation | Fresh exact-question routing, startup/account isolation and foreground deduplication. | 180-220 | A9 |
| A11 Native push teardown | Disable/revoke/offline-unsubscribe lifecycle and delivered-question replacement checks. | 180-220 | A5, A9 |

Final physical-device evidence belongs to existing
[#2392](https://github.com/danswett/agent-bridge/issues/2392), not a duplicate issue:
after A5/A10/A11 and authorized provisioning, exercise supported iOS/iPadOS devices
off-network in foreground, background and locked states. Record ten separately
displayed questions, ten finished turns and ten exact-question taps per notifying
client, plus three row runs per rehearsal stage, withdrawal, reconnect, renewal,
sign-out, revocation and failure receipts. Silent counts; suppressed notices and
provider acceptance do not. Keep tokens/keys/content out of published evidence.
Simulator/unit results do not qualify either row or change the #1396 gate.

## Reading boundary and platform references

Read whole at base `518a7d2826604a82cab0333ebcb97d4869c4463e`: Apple parity/client
docs, `Push.cs`, `WebPush.cs`, `PushGateway.cs`, `PushStore.cs`,
`BridgeNotificationManager.swift`, `AgentBridgeApp.swift` and `apple/project.yml`.
Read the complete S4 matrix and PWA PR 2416 change through plain git; read the
registration/send and notification sections, not all of `PushNotifier.cs`,
`v1-sessions.md` or `design.md`. No local build, test, quality run or device proof.

Apple references: [registration](https://developer.apple.com/documentation/usernotifications/registering-your-app-with-apns),
[provider requests](https://developer.apple.com/documentation/usernotifications/sending-notification-requests-to-apns)
and [service extensions](https://developer.apple.com/library/archive/documentation/NetworkingInternet/Conceptual/RemoteNotificationsPG/ModifyingNotifications.html).
Apple signing, entitlements, Keychain-group provisioning and provider-secret
deployment remain separately authorized prerequisites, not actions granted here.
