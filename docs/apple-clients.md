# Native Apple clients

The native SwiftUI clients live in [`apple/`](../apple/README.md). They share a
Swift 6 protocol library and tested client state across macOS 15+, iOS 18+ and
iPadOS 18+. XcodeGen builds both applications from `apple/project.yml`.

The workbench supports local Mac sockets and remote machines through the same
GitHub device sign-in, relay proof and Noise XX protocol used by Windows and the
PWA. Tokens and per-account device identities stay in Keychain. Remote session
JSON, attachment keys and signed commands travel inside authenticated channels.
Device sign-in polling observes a monotonic expiry deadline, including responses
that arrive after expiry, and rejects redirects and responses over 16 KiB. Tests
use private URL-loading fixtures and a controlled clock without contacting a relay.

Implemented client workflows include:

- Reconnecting session/usage/update subscriptions, sequence-gap recovery,
  canonical machine selection, offline roster and confirmed Forget.
- Needs You, global search, persistent pins/order/unread state, ended sessions,
  Markdown, attributed main-agent text, recent tools, selectable delegate details,
  nested/finished groups, latest-run outcome glyphs for idle background delegates,
  and draft-only requests to stop a delegate. Unknown outcomes follow the protocol's
  `done` fallback; absent outcomes remain unknown.
- Capability-gated questions, replies, two-step stop, attachments and photos;
  launch/resume, signed folder trust and cross-machine copy/resume.
- Usage across machines with a 14-day account-scoped cache, remote machine
  update checks/install requests, guarded notification answer/reply actions,
  native notification preferences, local host diagnostics and a Mac menu/login setting.

Requests retain their identity across uncertain delivery and reconnects. An
explicit `unconfirmed` response is never replayed; lost replies can be checked
only within the host's deduplication window and against the same host process.
Signed folder-trust questions display the full warning/path and are fetched again
before signing. Account changes revoke old state and cache work.

Large attachments use a 20-minute encrypted upload budget, followed by up to
20 minutes for the host to fetch and verify the blob. The client polls the same
`attachment.start` identity every 400 ms until verification completes, as the
[attachment protocol](protocol/v1-sessions.md#large-files-through-the-blob-store)
requires. Cancellation and failure attempt bounded cleanup on the original
connection; they never discard an attachment on a replacement connection.

Run `bash apple/scripts/verify.sh` for protocol/state tests, formatting and both
native builds. Cryptographic vectors are byte-identical to the .NET/WebCrypto
fixtures. Transport tests use in-memory encrypted peers and disposable local
sockets. A debug-only `--ui-fixture` launch exercises UI actions without opening
a host connection; its footer explicitly identifies simulated actions.
Blocking POSIX I/O in the disposable socket fixtures runs on Dispatch queues,
with continuations returning results to the tests. It must not occupy Swift's
cooperative executor, which also runs client callbacks and timeout tasks.

See [the parity checklist](apple-parity.md) for remaining work. This is not yet a
shipping-parity claim: a real .NET-host/relay interoperability run,
physical/minimum-OS devices, VoiceOver and APNs delivery remain acceptance work.
The current delivery target is local development builds; signing and distribution
are deliberately deferred. The Mac/Linux host also lacks answer/reply/stop/launch
backends and local host management; the client must respect those capability
limits rather than bypass them. Live hooks, Home Assistant and daemons are untouched.
