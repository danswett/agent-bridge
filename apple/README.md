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

The Mac app observes an already-running host's `default` profile. Open
**Agent Bridge > Settings…** or press **Command-comma** for the native, resizable
Settings window. Its **Connection** tab holds the host profile, relay and GitHub
sign-in; **General** holds reading, login-item and sample-session preferences;
**Notifications** and **About** have their own tabs. The menu-bar item and session
menu open the same window. Opening or closing Settings does not reconnect or
disconnect a host; connection edits still require **Apply and Reconnect**.
On iPhone and iPad these controls remain in the **Connection Settings** sheet.
The empty sidebar's sign-in, settings and sample-session actions stack vertically
when the sidebar cannot fit them in one row.
Sign in with the same GitHub account as your
hosts to connect remote machines on any Apple platform. The default relay is the
one used by the Windows app. Custom relays require HTTPS; authentication and blob
HTTP requests refuse redirects. Tokens and device keys are scoped to the relay
and GitHub account and stored in this installation's Keychain. Choose **Sign In with GitHub** and **Copy Code and Open GitHub**, paste the code, authorize Agent Bridge, then return to the app. The iOS app waits to exchange the code while it is in the background. A temporary network interruption retries the same code within its original expiry.

The app does not install, restart, migrate or reconfigure a local host. The Mac
client does not need a local host to show remote sessions. Connection status
distinguishes relay connection, a machine list not yet received, an empty list,
offline remote hosts and hosts whose encrypted channels are still connecting.
Settings shows the last machine-list receipt separately from the optional local
profile's status; a local timeout never replaces the remote status.
On Mac, hover over **Machines** for the live connection summary, or click it for
the full machine list. There is no persistent bottom status strip. Notification
action receipts and discovery warnings appear above the workbench only while
there is something to report; receipts retain their dismiss button.
The Mac
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
- Replies use a rounded multiline composer. The **+** menu contains **Attach File…**,
  **Choose Photo…**, and, on Mac, **Paste Photo**. Attachment progress, removal and
  errors stay with the draft. On Mac the plain + button positions the entire native
  menu above itself; all three attachment actions remain visible. A circular blue up-arrow
  sends from inside the
  composer's lower-right corner; **Command-Return** still sends. The footer shows
  the actual session status and reported model, not an editable model or a claim
  of elevated permissions. Offline, read-only, uploading and uncertain-delivery
  safeguards still apply.
  On Mac it hugs its content at the bottom: the 280-point limit is a growth
  ceiling, not reserved blank space. Long drafts and questions scroll within it.
- While a live host reports an active turn with no new main-agent response, the
  response pane shows **Host is working…** with a subtle text shimmer. Previous
  response text stays visible and is labelled as previous. New main-agent text,
  a question, idle/ending status or a lost connection stops the indicator; delegate
  output does not stand in for the main response. A first snapshot containing
  response text is not assumed stale. Reduce Motion uses a static label, and
  hidden/inactive views do not run the shimmer.
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

`bash scripts/test-mac-attachment-menu.sh` also runs the native attachment-control
checks by itself. It constructs the real control offscreen and checks its upper
menu geometry, visible actions, plain-button hosting, action routing and disabled state without opening a picker
or uploading a file.

An open tool-history pane refreshes after the host reconnects even when the session's
`updatedAt` value has not changed. Reads from an old connection cannot replace that
refreshed history, and reconnecting never sends or retries a session mutation.

For the native Mac settings and sidebar layout, also check the running client:

- With the workbench active, **Command-comma** and the app menu's **Settings…**
  open the same independent Settings window. Repeating the command reuses it.
- Connection, General, Notifications and About remain reachable. Closing Settings
  leaves the workbench open and does not disconnect a host. Unsaved connection
  fields are not applied just by opening Settings or switching tabs.
- In the empty sidebar, narrow the sidebar to its minimum width. Sign-in, Settings
  and sample-session actions must occupy separate rows inside the sidebar, with
  complete labels. When there is room for the complete row, it can be horizontal.
- Starting sign-in from the workbench opens the Connection tab even if a different
  Settings tab was selected before. Do not use a live account for automated checks.

Validated locally with Xcode 27.2 / Swift 6.4.2. The Mac UI was exercised through
accessibility against the isolated fixture; iPhone/iPad layouts are checked in
simulators. A signed physical iPad completed GitHub authorization and displayed
live sessions, the inline editor and the inspector through the public relay.
Unsigned builds do not establish release signing or Keychain/device
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
