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
the runnable Mac app and physical mobile devices. `project.yml` is authoritative;
generated projects are ignored. To build the Mac app from a terminal:

```sh
DEVELOPMENT_TEAM=YOUR_TEAM_ID bash scripts/build-macos.sh
```

Apple Development signing is required even for local Mac use: the data protection
Keychain requires a provisioned application identity. The Mac target declares one
private Keychain group, and this script verifies the signed app identity, group
and embedded development profile. An unsigned compile-check build cannot sign in;
error `-34018` means its required entitlement is missing, not that the Mac is locked.

The Mac app observes an already-running host's `default` profile. Change the
profile in **Connection Settings**. Sign in with the same GitHub account as your
hosts to connect remote machines on any Apple platform. The default relay is the
one used by the Windows app. Custom relays require HTTPS; authentication and blob
HTTP requests refuse redirects. Tokens and device keys are scoped to the relay
and GitHub account and stored in this installation's Keychain. Choose **Sign In with GitHub** and **Copy Code and Open GitHub**, paste the code, authorize Agent Bridge, then return to the app. The iOS app waits to exchange the code while it is in the background. A temporary network interruption retries the same code within its original expiry.

The app does not install, restart, migrate or reconfigure a local host. The Mac
host supports actions on sessions launched by its own managed terminal backend;
existing sessions remain read-only. Linux delivery remains unsupported. Actions
work only when the connected host offers them.

## Workflows

- Local and encrypted remote fleet feeds; reconnect, snapshots and sequence-gap
  recovery; usage/update snapshots and events retain wire order during recovery
  and manual refresh; explicit offline state and canonical local/remote identity.
- Global search, Needs You, pins, persistent manual/recent/machine order, unread
  state, recently ended sessions, Markdown, reasoning and recent tools. Delegate
  details show task/latest text, reasoning, tool durations, nesting and finished
  groups; a stop request prepares an ID-only reply without sending it.
- The Mac workbench keeps the reply or question editor below the conversation. A
  resizable right pane shows details and tools; selecting a delegate opens its
  activity in that pane while the conversation and composer stay visible.
  iPad uses the same side-by-side arrangement when its detail column fits both
  panes. On iPhone and narrow iPad windows, the editor stays below the
  conversation and the toolbar opens the inspector.
- Structured questions/approvals, replies and two-step stop. An uncertain action
  remains bound to its original UUID and host process; reconnect never silently
  sends a fresh press. A delivered composer reply removes only its sent prefix
  from the saved draft once, preserving text appended while delivery was pending.
  Question answers and notification replies have separate draft state. Replies
  check the complete encoded 48 KiB request budget before delivery; answers also
  check the host's 16 KiB budget using its pinned .NET 10 JSON escaping rules.
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
integration. Mac self-updates and host installation remain separate platform work.
Native login-item registration still needs OS acceptance. The Windows
Task Scheduler/registry/Velopack implementations are not Apple platform APIs.
