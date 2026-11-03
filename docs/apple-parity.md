# Apple client parity

Source comparison for the native macOS 15+, iOS 18+ and iPadOS 18+ clients,
November 2, 2026, including the delegate inspector in Windows commit `1ec2eb0`
(#250). This describes implemented Windows behavior, not its roadmap.
The current delivery scope is **local development builds**, as chosen by the
user. Signed distribution is deferred by choice and is not a completion condition
for that scope.

“Implemented” below means the client code exists and has local automated or
fixture coverage. It does not mean an Apple build has completed an end-to-end
operation against a real relay and .NET host. That acceptance work is listed
separately.

## Workbench previews

The [iPad simulator](images/apple-workbench/ipad-simulator.png) shows the session list,
question editor and inspector together. The [iPhone simulator](images/apple-workbench/iphone-simulator.png)
shows a reply draft above the keyboard. Both captures use the offline demo fixture;
live acceptance is recorded separately below.

## Implemented workflows

| Area | Windows implementation | Apple implementation and limits |
| --- | --- | --- |
| Local sessions and reconnect | [HostConnection](../src/AgentBridge.App.Core/HostConnection.cs) | [LocalBridge](../apple/Sources/AgentBridgeAppleCore/LocalBridge.swift), [SessionStore](../apple/Sources/AgentBridgeAppleCore/SessionStore.swift), [HostStore](../apple/State/HostStore.swift): macOS Unix socket, framed requests, subscriptions, snapshot completion, sequence-gap recovery and reconnect. iOS uses remote connections. |
| Remote fleet and identity | [RelayClient](../src/AgentBridge.Transport/RelayClient.cs), [Machines](../src/AgentBridge.App.Core/Machines.cs) | [RelayBridge](../apple/Sources/AgentBridgeAppleCore/RelayBridge.swift), [RelaySignIn](../apple/Sources/AgentBridgeAppleCore/RelaySignIn.swift), [BridgeKeychain](../apple/Sources/AgentBridgeAppleCore/BridgeKeychain.swift): GitHub device sign-in, account/relay-scoped credentials and device keys, relay authentication, authenticated Noise channels and multiple machines. The signed Mac development client has shown live sessions through the public relay; device mutation acceptance is tracked below. |
| Session navigation | [SessionOrder](../src/AgentBridge.App.Core/SessionOrder.cs), [Search](../src/AgentBridge.App.Core/MainViewModel.Search.cs), [Needs You](../src/AgentBridge.App.Core/MainViewModel.NeedsYou.cs) | [SessionWorkbench](../apple/Apps/SessionWorkbench.swift), [BridgeStore](../apple/State/BridgeStore.swift): Needs You, global multi-term search and Command-K, machine filtering, pins, manual/recent/machine order, unread markers and recently ended sessions. Manual movement uses Move Up/Down. Preferences are scoped to the active connection/account. |
| Reading and metadata | [MarkdownModel](../src/AgentBridge.App.Core/MarkdownModel.cs), [Session panes](../src/AgentBridge.App.Core/SessionCardViewModel.Panes.cs) | [SessionDetail](../apple/Apps/SessionDetail.swift), [BridgeMarkdown](../apple/Apps/BridgeMarkdown.swift), [SessionPresentation](../apple/Sources/AgentBridgeAppleCore/SessionPresentation.swift): selectable response/question text, native headings/lists/tables/rules/quotes/fenced and indented code, Markdown reasoning and metadata; persisted reasoning/tool visibility preference. Parsing is bounded; images never load and HTML never executes. Remaining reading differences are below. |
| Recent tools and delegates | [Delegate presentation](../src/AgentBridge.App.Core/Delegates.cs), [attribution](../src/AgentBridge.App.Core/Attribution.cs), snapshot `1ec2eb0` (#250) | [SessionHistoryView](../apple/Apps/SessionHistoryView.swift), [DelegateInspector](../apple/Apps/DelegateInspector.swift), [BridgeHistoryLoader](../apple/Sources/AgentBridgeAppleCore/BridgeHistoryLoader.swift): latest 50 tools, coalesced refresh, selectable delegates, task/latest/reasoning, metadata and tool durations, nested/finished groups, and start/report events. Delegate reads require the selected ID and delegate-shaped response; safe role labels distinguish delegates from the main agent. Ask main agent to stop it prepares an ID-only reply for review and never sends it. The Mac workbench has a resizable right pane: details precede tools, and selecting a delegate replaces those details in place. A wide iPad also shows the conversation, composer and inspector together. Narrow iPad windows and iPhone keep the editor on screen and open the inspector from the toolbar. The main-session End action and its receipt are shown only in the session inspector, never in delegate details. Neither client provides a complete conversation transcript. |
| Answers, replies and stop | [QuestionViewModel](../src/AgentBridge.App.Core/QuestionViewModel.cs), [ReplyViewModel](../src/AgentBridge.App.Core/ReplyViewModel.cs), [EndSessionViewModel](../src/AgentBridge.App.Core/EndSessionViewModel.cs) | [SessionActions](../apple/Apps/SessionActions.swift), [BridgeQuestion](../apple/Sources/AgentBridgeAppleCore/BridgeQuestion.swift), [HostStore](../apple/State/HostStore.swift): capability-gated structured questions/permissions, replies with in-memory drafts, two-step stop and explicit receipts. Lost outcomes retain their UUID and original host process; explicit unconfirmed outcomes are not automatically replayed. |
| Attachments | [ReplyViewModel](../src/AgentBridge.App.Core/ReplyViewModel.cs), [LaunchViewModel](../src/AgentBridge.App.Core/LaunchViewModel.cs) | [AttachmentComposer](../apple/Apps/AttachmentComposer.swift), [BridgeAttachmentUploader](../apple/State/BridgeAttachmentUploader.swift), [RelayBlobUpload](../apple/Sources/AgentBridgeAppleCore/RelayBlobUpload.swift): file import, PhotosPicker, Mac image paste, bounded resizing/uploads, staged removal, local chunks and encrypted remote blobs. Staged IDs are invalidated when the connection changes. |
| Launch and resume | [LaunchViewModel](../src/AgentBridge.App.Core/LaunchViewModel.cs) | [LaunchView](../apple/Apps/LaunchView.swift), [BridgeLaunch](../apple/Sources/AgentBridgeAppleCore/BridgeLaunch.swift): host-provided agents/workspaces/models/effort/context/profiles, prompt, attachments, local resume and per-machine in-memory draft choices. Pending actions remain bound to the physical machine across local/relay selection. |
| Folder trust and copy/resume | [Launch trust](../src/AgentBridge.App.Core/LaunchViewModel.Trust.cs), [Transfers](../src/AgentBridge.App.Core/Transfers.cs) | [FolderTrustView](../apple/Apps/FolderTrustView.swift), [BridgeSignedCommand](../apple/Sources/AgentBridgeAppleCore/BridgeSignedCommand.swift), [BridgeTransfer](../apple/Sources/AgentBridgeAppleCore/BridgeTransfer.swift): full warning/path/repository disclosure, refresh before signing, target/source binding and signed relay-only trust or cross-machine copy/resume. Local targets use their authenticated relay connection. Local host approval configuration is unavailable on the Mac host. |
| Usage and retained readings | [UsageViewModel](../src/AgentBridge.App.Core/UsageViewModel.cs), [UsageCache](../src/AgentBridge.App.Core/UsageCache.cs) | [UsageView](../apple/Apps/UsageView.swift), [BridgeUsageCache](../apple/State/BridgeUsageCache.swift), [BridgeUsageLifecycle](../apple/State/BridgeUsageLifecycle.swift): allowance windows, severity, pace, reset time, source/age, per-host refresh, cross-machine readings and 14-day account-scoped cache. Cache leases and roster reconciliation reject stale account work. |
| Machine roster and updates | [MachineRows](../src/AgentBridge.App.Core/MachineRows.cs), [MachineUpdates](../src/AgentBridge.App.Core/MachineUpdates.cs), [Check all](../src/AgentBridge.App.Core/MainViewModel.cs) | [MachinesView](../apple/Apps/MachinesView.swift): canonical online/offline machines, read-only local host diagnostics, confirmed offline Forget, per-host and Check All update checks, progress/results, and individually confirmed installs. Checks skip unavailable, busy or uncertain hosts and retain per-host receipts. This controls capable remote host installers; it does not update the Apple app itself. |
| Notifications and app presence | [Toast planning](../src/AgentBridge.App.Core/Toasts.cs), [Windows toasts](../src/AgentBridge.App/Toasts.cs) | [BridgeNotificationManager](../apple/State/BridgeNotificationManager.swift), [SettingsNotificationsView](../apple/Apps/SettingsNotificationsView.swift), [AgentBridgeApp](../apple/Apps/AgentBridgeApp.swift): opt-in question/finished notices, quiet mode, private previews, session navigation, withdrawal/deduplication, Mac Dock badge and menu-bar access, plus an explicit native Open at Login preference. Login registration follows [Apple Service Management](https://developer.apple.com/documentation/servicemanagement/smappservice) and is not exercised by tests. Native question/reply actions and visible receipts use the same guarded mutations as the workbench. iOS background delivery remains a platform dependency below. |

## Native behavior differences

Notification actions require the app to come to the foreground and the device to
be unlocked. Simple choices/approvals are offered only with an opted-in complete
question preview; full forms, Other answers and more than three choices open the
session. Free-text answers and replies use native text-input actions. Delegate-only waits do not reannounce an unchanged old main-agent response,
including after reconnect. Every press
is single-use and revalidates the account, current subscription, a fresh
`session.get`, capabilities and connection before sending. Notification reply text
does not consume the saved workbench draft, including after an explicit retry.
Stale/offline actions
show a Not sent receipt and never queue a later mutation. Windows can act directly
from a background tray process; iOS background delivery is the dependency below.

The host's read-only reason disables known action capabilities and prevents new
mutation attempts or retries, even if its advertised capabilities still list the
action. Session/usage inspection and staged-attachment cleanup remain available.

## Platform and host dependencies

- **Mac host actions:** [Program.cs](../src/AgentBridge.Host/Program.cs) now shares
  a managed tmux backend for launch, answer, reply and stop. Only panes launched
  by the current host run can be controlled; existing Terminal sessions are not
  adopted. Native Codex lock discovery rechecks process ownership and file
  descriptors. See [Mac host setup](macos-host.md) and the actual acceptance
  limitations below. Linux delivery remains unsupported.
- **Mac local administration:** the native socket peer does not yet establish
  administrative caller identity ([local-pipe.md](protocol/local-pipe.md)), so
  app-based host configuration/sign-in/stop/restart remain unavailable. The
  operator CLI supports configuration and sign-in from the owner's Terminal;
  guard #67 still refuses those from an agent session. Hook takeover, Windows
  Dev Box keep-awake and Task Scheduler behavior remain platform-specific.
- **iOS/iPadOS background notifications:** current notices require the app to be
  open. APNs provider, device-token registration, entitlements and lifecycle
  validation are missing. A Windows tray process is not an iOS background
  transport.
- **Distribution and self-updates:** signed Mac packages, notarization,
  TestFlight/App Store delivery, Apple self-update policy and bundled host
  installation are deferred by the local-development-build decision. Windows
  Velopack, registry and Task Scheduler code does not supply these Apple pieces.
  Apple Development signing can be used for local physical-device builds.
  Remote host update controls are already implemented independently.

## Acceptance still required

Local tests cover protocol/cryptographic vectors, encrypted in-memory peers,
disposable sockets, stream corruption, snapshots, cancellation/reconnect,
mutation identity, drafts, account/cache isolation and notification planning.
Both native targets must pass the repository's
[`verify.sh`](../apple/scripts/verify.sh). Debug UI fixtures exercise simulated
actions only and are not evidence of delivery to an agent.

The local Mac fixture was exercised through question/reply receipts, confirmed
stop, launch, nested and finished delegate reads, and the draft-only delegate
stop request. Traversing the nested inspector exposed an AppKit accessibility
recursion; limiting selection to prose and IDs and providing explicit control
labels fixed the reproduced path. This is a targeted regression check, not a
substitute for the full VoiceOver acceptance below.

The signed Mac client was also checked with two connected machines and live sessions. Typing into the persistent reply field enabled Send; the draft was cleared without sending a test into an existing session. This checks the live layout and capability state, not delivery.

On a physical iPad, switching to Safari for device authorization exposed a lost
connection during token polling. After the retry fix, the owner completed GitHub
authorization. The signed app showed two connected machines and two live sessions;
selecting one showed the conversation, an editable reply with a staged image,
and the session inspector. This verifies physical sign-in, discovery, session
data and composer presentation. It does not establish delivery of that draft.

A later [physical reply and recovery test](apple-native-interop-acceptance.md#physical-reply-and-receipt-recovery) launched a fresh Copilot session on the Mac, confirmed one delivered user turn, and verified a lost-receipt retry without a second turn.

Still pending: the remaining real .NET-host/relay interoperability cases covering
multi-machine reconnect, answer and stop receipts, attachment delivery, other launch flows,
folder trust, copy/resume and remote update requests; OS notification
authorization/delivery and Mac login registration; physical iPhone and minimum-supported-OS devices; and VoiceOver
plus large-text interaction testing. These are validation gaps for implemented
code, distinct from the platform dependencies above. Live hooks, Home Assistant
and installed daemons must remain untouched. Final-head CI, Copilot review and
independent security review remain PR merge requirements.
