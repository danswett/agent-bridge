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

## Implemented workflows

| Area | Windows implementation | Apple implementation and limits |
| --- | --- | --- |
| Local sessions and reconnect | [HostConnection](../src/AgentBridge.App.Core/HostConnection.cs) | [LocalBridge](../apple/Sources/AgentBridgeAppleCore/LocalBridge.swift), [SessionStore](../apple/Sources/AgentBridgeAppleCore/SessionStore.swift), [HostStore](../apple/State/HostStore.swift): macOS Unix socket, framed requests, subscriptions, snapshot completion, sequence-gap recovery and reconnect. iOS uses remote connections. |
| Remote fleet and identity | [RelayClient](../src/AgentBridge.Transport/RelayClient.cs), [Machines](../src/AgentBridge.App.Core/Machines.cs) | [RelayBridge](../apple/Sources/AgentBridgeAppleCore/RelayBridge.swift), [RelaySignIn](../apple/Sources/AgentBridgeAppleCore/RelaySignIn.swift), [BridgeKeychain](../apple/Sources/AgentBridgeAppleCore/BridgeKeychain.swift): GitHub device sign-in, account/relay-scoped credentials and device keys, relay authentication, authenticated Noise channels and multiple machines. Real service interoperability remains to be exercised. |
| Session navigation | [SessionOrder](../src/AgentBridge.App.Core/SessionOrder.cs), [Search](../src/AgentBridge.App.Core/MainViewModel.Search.cs), [Needs You](../src/AgentBridge.App.Core/MainViewModel.NeedsYou.cs) | [SessionWorkbench](../apple/Apps/SessionWorkbench.swift), [BridgeStore](../apple/State/BridgeStore.swift): Needs You, global multi-term search and Command-K, machine filtering, pins, manual/recent/machine order, unread markers and recently ended sessions. Manual movement uses Move Up/Down. Preferences are scoped to the active connection/account. |
| Reading and metadata | [MarkdownModel](../src/AgentBridge.App.Core/MarkdownModel.cs), [Session panes](../src/AgentBridge.App.Core/SessionCardViewModel.Panes.cs) | [SessionDetail](../apple/Apps/SessionDetail.swift), [BridgeMarkdown](../apple/Apps/BridgeMarkdown.swift), [SessionPresentation](../apple/Sources/AgentBridgeAppleCore/SessionPresentation.swift): selectable response/question text, native headings/lists/tables/rules/quotes/fenced and indented code, Markdown reasoning and metadata; persisted reasoning/tool visibility preference. Parsing is bounded; images never load and HTML never executes. Remaining reading differences are below. |
| Recent tools and delegates | [Session panes](../src/AgentBridge.App.Core/SessionCardViewModel.Panes.cs) | [SessionHistoryView](../apple/Apps/SessionHistoryView.swift), [BridgeHistoryLoader](../apple/Sources/AgentBridgeAppleCore/BridgeHistoryLoader.swift): latest 50 tools, refresh on session updates while expanded, coalesced in-flight requests and manual refresh. Background summaries show both agent and command counts; delegates show names/status/lines when supplied. Windows currently shows background counts, not interactive delegate history. Neither client provides a complete conversation transcript. |
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
`session.get`, capabilities and connection before sending. Stale/offline actions
show a Not sent receipt and never queue a later mutation. Windows can act directly
from a background tray process; iOS background delivery is the dependency below.

## Platform and host dependencies

- **Mac/Linux host actions and local administration:** the host selects
  unsupported answer/reply/stop/launch implementations off Windows
  ([Program.cs](../src/AgentBridge.Host/Program.cs)). Mac local host management
  operations are explicitly unavailable ([local-pipe.md](protocol/local-pipe.md)).
  Host install/start/stop/restart, remote-approval configuration and approved-device
  management require host/platform work; the Apple client must not bypass these
  restrictions. A capable remote Windows host can offer the corresponding
  session actions. Windows Dev Box keep-awake and Task Scheduler behavior are
  platform-specific.
- **iOS/iPadOS background notifications:** current notices require the app to be
  open. APNs provider, device-token registration, entitlements and lifecycle
  validation are missing. A Windows tray process is not an iOS background
  transport.
- **Distribution and self-updates:** signed Mac packages, notarization,
  TestFlight/App Store delivery, Apple self-update policy and bundled host
  installation are deferred by the local-development-build decision. Windows
  Velopack, registry and Task Scheduler code does not supply these Apple pieces.
  Remote host update controls are already implemented independently.

## Acceptance still required

Local tests cover protocol/cryptographic vectors, encrypted in-memory peers,
disposable sockets, stream corruption, snapshots, cancellation/reconnect,
mutation identity, drafts, account/cache isolation and notification planning.
Both native targets must pass the repository's
[`verify.sh`](../apple/scripts/verify.sh). Debug UI fixtures exercise simulated
actions only and are not evidence of delivery to an agent.

Still pending: a disposable real .NET-host/relay interoperability pass covering
sign-in, multi-machine reconnect, answer/reply/stop receipts, attachments, launch,
folder trust, copy/resume and remote update requests; OS notification
authorization/delivery and Mac login registration; physical and minimum-supported-OS devices; and VoiceOver
plus large-text interaction testing. These are validation gaps for implemented
code, distinct from the platform dependencies above. Live hooks, Home Assistant
and installed daemons must remain untouched. Final-head CI, Copilot review and
independent security review remain PR merge requirements.
