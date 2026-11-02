# Agent Bridge — native Apple clients

Swift 6 protocol and state libraries, with SwiftUI apps for macOS 15+, iOS 18+
and iPadOS 18+.

## Run

Install Xcode, XcodeGen and SwiftFormat (`brew install xcodegen swiftformat`):

```sh
xcodegen generate
open AgentBridgeApple.xcodeproj
```

Select **AgentBridgeMac** or **AgentBridgeMobile**. Choose a signing team for
physical devices. `project.yml` is authoritative; generated projects are ignored.

The Mac app observes an already-running host's `default` profile. Change the
profile in **Connection Settings**. Sign in with the same GitHub account as your
hosts to connect remote machines on any Apple platform. The default relay is the
one used by the Windows app. Custom relays require HTTPS; authentication and blob
HTTP requests refuse redirects. Tokens and device keys are scoped to the relay
and GitHub account and stored in this installation's Keychain.

The app does not install, restart, migrate or reconfigure a local host. Mac/Linux
hosts currently advertise read-only session capabilities. Actions work only when
the connected host offers them, such as a capable remote Windows host.

## Workflows

- Local and encrypted remote fleet feeds; reconnect, snapshots and sequence-gap
  recovery; explicit offline state and canonical local/remote machine identity.
- Global search, Needs You, pins, persistent manual/recent/machine order, unread
  state, recently ended sessions, Markdown, reasoning, recent tools and delegates.
- Structured questions/approvals, replies and two-step stop. An uncertain action
  remains bound to its original UUID and host process; reconnect never silently
  sends a fresh press.
- Files, PhotosPicker and Mac image paste, resizing when needed, removable staged
  attachments, bounded chunk uploads and encrypted blob uploads up to host limits.
- Host-provided launch/resume choices, signed full-disclosure folder-trust answers,
  and signed copy-and-resume between remote machines. Local targets use an
  authenticated relay connection for signed operations.
- Usage and machine updates, confirmed offline-machine Forget, account-scoped
  usage retention, opt-in notifications, quiet/private previews, a Mac menu,
  local host diagnostics and an explicit Open at Login preference. Notification
  answer/reply actions revalidate the current question, account and live connection,
  then show a delivery receipt; stale actions never queue a later send.

**Explore Sample Sessions** is offline and clearly labelled. `--sample-sessions`
is the equivalent launch argument. Debug builds also support `--ui-fixture` for
interactive, simulated answer/reply/stop/launch receipts. That fixture opens no
socket, starts no process and types no keys into agents. Never use it as evidence
of a live host connection.

## Verify

```sh
bash scripts/verify.sh
```

This runs all protocol/state tests, SwiftFormat lint and unsigned macOS/iOS
Simulator builds. Tests cover upstream Noise/command/blob vectors, malicious
frames, response ordering, cancellation, final socket data, reconnect races,
mutation identity, attachments, cache isolation and delayed account transitions.
Fixtures use disposable sockets, files and in-memory peers, never installed
profiles or live agents.

Validated locally with Xcode 27.2 / Swift 6.4.2. The Mac UI was exercised through
accessibility against the isolated fixture; iPhone/iPad layouts are checked in
simulators. Unsigned builds do not establish release signing or Keychain/device
entitlement readiness. Real relay/.NET-host interoperability, OS notification
permission/delivery, minimum-OS devices and VoiceOver still need acceptance tests.

## Local development scope

The current delivery target is local development builds only. App Store/TestFlight,
notarization, signed installers and release update channels are deferred by choice.


See [`docs/apple-parity.md`](../docs/apple-parity.md). iOS notifications currently
operate while the app is open; background APNs needs provider and entitlement
integration. Mac self-updates, host installation and the missing Mac host action backends remain
separate platform work. Native login-item registration still needs OS acceptance. The Windows
Task Scheduler/registry/Velopack implementations are not Apple platform APIs.
