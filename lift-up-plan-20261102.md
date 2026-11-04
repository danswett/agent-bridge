# Lift-Up Plan: Agent Bridge on Mac, iPhone and iPad

> **Historical Apple-only audit.** The [whole-product plan and implementation record](lift-up-plan-20261102-2.md) supersedes this snapshot. Its observations and open tasks describe the baseline, before the physical iPad delivery test.

> Platform: Mixed — native SwiftUI clients on macOS 15+ and iOS/iPadOS 18+, backed by the .NET Mac host.
> Surveyed: 2026-11-02
> Baseline: codex/apple-full-bridge@f0bf103 — clean tree before report; fetched origin/main@68972ac. Branch contains main and is 86 commits ahead, zero behind. Final HEAD recheck unchanged.
> Scope: Apple clients and their Mac-host bridge on the combined PR branch. Windows is the parity reference, not a second implementation under audit.
> Coverage: Partial static review of workbench, account/connection lifecycle, protocol client, notifications, attachment handling and managed tmux delivery. No new device run, benchmark, dependency CVE scan or relay infrastructure audit. CI and merge status are outside this report.
> Attractiveness anchor: Inferred — the Windows Agent Bridge workbench for the layout; native Apple split views, keyboard conventions and Dynamic Type for execution.

The branch records physical iPad sign-in, two live sessions, an editable composer with a staged image, and an inspector. It does not record delivery of that draft. The first move below closes that specific gap; the remaining items are ranked within each dimension. Effort and impact are relative estimates, not benchmark results.

## Performance

### PERF-1. Avoid rebuilding two lists to find one selection

**Location:** `apple/Apps/SessionWorkbench.swift:15`, `apple/State/BridgeStore.swift:78`

**Proof:**

apple/Apps/SessionWorkbench.swift:15
```
    private var selected: SessionEntry? {
        (store.entries + store.endedEntries).first { $0.id == store.selectedSessionID }
    }
```

apple/State/BridgeStore.swift:78
```
    var entries: [SessionEntry] {
        if isSample {
            return SampleSessions.sessions.map { SessionEntry(session: $0, host: nil) }
        }
        return canonicalHosts.flatMap { host in host.sessions.map { SessionEntry(session: $0, host: host) } }
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'entries\.lazy|endedEntries\.first|sessionEntryByID' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
# (no matches)
$ rg -n -F -- 'private var selected: SessionEntry? {' apple/Apps/SessionWorkbench.swift
15:    private var selected: SessionEntry? {
$ rg -n -F -- 'var entries: [SessionEntry] {' apple/State/BridgeStore.swift
78:    var entries: [SessionEntry] {
```

**Do:** Resolve the selected item by searching live hosts first and ended sessions only on a miss. Compare allocations when switching between sessions in a populated sidebar.

**Why:** Each lookup currently constructs the live and ended arrays and concatenates them before testing the first ID.

**Effort:** S · **Impact:** M

### PERF-2. Build the order membership set once per host update

**Location:** `apple/State/BridgeStore.swift:682`

**Proof:**

apple/State/BridgeStore.swift:682
```
    private func registerSessions(_ sessions: [BridgeSession], host: HostStore) {
        let machineID = host.hello?["machine"]?["id"]?.string ?? host.id
        let newSessions = sessions.filter { !manualOrder.contains(machineID + ":" + $0.id) }.sorted { ($0.updatedAt ?? "") > ($1.updatedAt ?? "") }
        guard !newSessions.isEmpty else { return }
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'Set\(manualOrder\)|orderMembership' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
apple/State/BridgeStore.swift:694:        let kept = Set(manualOrder)
$ rg -n -F -- 'private func registerSessions(_ sessions: [BridgeSession], host: HostStore) {' apple/State/BridgeStore.swift
682:    private func registerSessions(_ sessions: [BridgeSession], host: HostStore) {
```

**Do:** Use a set for the membership check before sorting newly observed sessions. Keep the array for presentation order.

**Why:** Each incoming session scans up to 300 order entries; the set currently built later cannot reduce that work.

**Effort:** S · **Impact:** M

### PERF-3. Reuse search ranking keys

**Location:** `apple/Apps/SessionWorkbench.swift:35`

**Proof:**

apple/Apps/SessionWorkbench.swift:35
```
            let query = search.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            return matched.sorted {
                let leftPrefix = $0.session.title.lowercased().hasPrefix(query)
                let rightPrefix = $1.session.title.lowercased().hasPrefix(query)
                if leftPrefix != rightPrefix {
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'prefixRank|searchRank|normalizedTitle' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
# (no matches)
$ rg -n -F -- 'let query = search.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()' apple/Apps/SessionWorkbench.swift
35:            let query = search.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
```

**Do:** Compute the title prefix match once per candidate before sorting. Verify the order stays the same for equal timestamps and mixed-case queries.

**Why:** The sort comparator lowercases both titles repeatedly during one search. No latency measurement is assumed.

**Effort:** S · **Impact:** M

### PERF-4. Stop waking the delegate inspector when it has nothing to refresh

**Location:** `apple/Apps/DelegateInspector.swift:160`, `apple/Apps/DelegateInspector.swift:169`

**Proof:**

apple/Apps/DelegateInspector.swift:160
```
            while !Task.isCancelled {
                do { try await Task.sleep(for: .seconds(2)) } catch { return }
                refreshIfDue()
            }
        }
```

apple/Apps/DelegateInspector.swift:169
```
        guard let entry else { return }
        let elapsed = Date.now.timeIntervalSince(lastRequested)
        if elapsed >= 2, needsRefresh || loader.shouldRetry || entry.isRunning && elapsed >= 10 {
            refresh()
        }
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- '\.task\(id:|TimelineView|ContinuousClock' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
apple/Apps/LaunchView.swift:201:            .task(id: machineID) { await loadOptions() }
apple/Sources/AgentBridgeAppleCore/BridgeBlobStaging.swift:14:            let clock = ContinuousClock()
apple/State/HostStore.swift:18:    let firstSentAt = ContinuousClock.now
apple/Apps/UsageView.swift:14:        TimelineView(.periodic(from: .now, by: 30)) { context in
apple/Apps/UsageView.swift:41:        .task(id: refreshID) {
apple/State/BridgeAttachmentUploader.swift:45:    private let progressClock: @Sendable () -> ContinuousClock.Instant
apple/State/BridgeAttachmentUploader.swift:50:    init(progressClock: @escaping @Sendable () -> ContinuousClock.Instant) {
apple/State/BridgeAttachmentUploader.swift:112:            var lastProgress: ContinuousClock.Instant?
apple/Apps/LocalHostDetailsView.swift:56:            .task(id: host.connectionID) { await refresh() }
apple/Sources/AgentBridgeAppleCore/RelaySignIn.swift:69:    private let now: @Sendable () -> ContinuousClock.Instant
apple/Sources/AgentBridgeAppleCore/RelaySignIn.swift:80:    init(relay: String, session: URLSession, now: @escaping @Sendable () -> ContinuousClock.Instant = { ContinuousClock.now },
apple/Apps/DelegateInspector.swift:47:        TimelineView(.periodic(from: .now, by: 10)) { clock in
$ rg -n -F -- 'while !Task.isCancelled {' apple/Apps/DelegateInspector.swift
160:            while !Task.isCancelled {
$ rg -n -F -- 'guard let entry else { return }' apple/Apps/DelegateInspector.swift
169:        guard let entry else { return }
```

**Do:** Schedule one refresh for the next due time and stop scheduling after the delegate finishes unless its content changes or a retry is due. Verify idle wakeups separately from actual requests.

**Why:** The visible inspector wakes every two seconds even when the inner predicate decides against a network call.

**Effort:** M · **Impact:** M

### PERF-5. Compute the menu-bar question count once

**Location:** `apple/Apps/AgentBridgeApp.swift:30`

**Proof:**

apple/Apps/AgentBridgeApp.swift:30
```
            MenuBarExtra {
                BridgeMenuView(store: store)
            } label: {
                Label(store.entries.filter { $0.session.question != nil }.isEmpty ? "Agent Bridge" : "\(store.entries.filter { $0.session.question != nil }.count)", systemImage: "terminal")
            }
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'pendingQuestionCount|waitingQuestionCount|lazy\.filter' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
# (no matches)
$ rg -n -F -- 'MenuBarExtra {' apple/Apps/AgentBridgeApp.swift
30:            MenuBarExtra {
```

**Do:** Compute one count for the menu label and use it for both the empty and nonempty text. Profile the nonzero case with several machines.

**Why:** The label filters a freshly computed session array twice whenever a question is pending.

**Effort:** S · **Impact:** S

## Functionality

### FUNC-1. Launch the first session from a connected empty workbench

**Location:** `apple/Apps/SessionWorkbench.swift:102`, `apple/Apps/SessionWorkbench.swift:283`

**Proof:**

apple/Apps/SessionWorkbench.swift:102
```
                            Button("Reconnect", systemImage: "arrow.clockwise", action: store.connect).disabled(store.isSample || store.isFixture)
                            Button("Launch or Resume", systemImage: "plus") { showingLaunch = true }
                                .disabled(!store.hosts.contains { $0.can("sessions.launch") || $0.capabilities.contains("launch.trust") || $0.attempts["launch"] != nil })
                        } label: { Image(systemName: "line.3.horizontal.decrease") }
                            .accessibilityLabel("Session filters and connections")
```

apple/Apps/SessionWorkbench.swift:283
```
            Button("Connection Settings") { showingConnection = true }
            if !store.isSample {
                Button("Explore Sample Sessions", action: store.showSamples)
            }
        }
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'Button\("Launch|showingLaunch = true' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
apple/Apps/SessionWorkbench.swift:103:                            Button("Launch or Resume", systemImage: "plus") { showingLaunch = true }
$ rg -n -F -- 'Button("Reconnect", systemImage: "arrow.clockwise", action: store.connect).disabled(store.isSample || store.isFixture)' apple/Apps/SessionWorkbench.swift
102:                            Button("Reconnect", systemImage: "arrow.clockwise", action: store.connect).disabled(store.isSample || store.isFixture)
$ rg -n -F -- 'Button("Connection Settings") { showingConnection = true }' apple/Apps/SessionWorkbench.swift
283:            Button("Connection Settings") { showingConnection = true }
```

**Do:** Put a primary “Launch or Resume” action (new placement) in the connected empty state on Mac and iPad. Share the existing toolbar eligibility rule, including pending launches; verify an eligible empty host opens the chooser and a read-only host still explains its limits.

**Why:** The existing launcher is behind the filter icon. The empty screen offers settings and samples instead, so zero sessions can look like a failed connection.

**Effort:** S · **Impact:** L

### FUNC-2. Respect the selected machine in the attention section

**Location:** `apple/Apps/SessionWorkbench.swift:19`, `apple/Apps/SessionWorkbench.swift:30`

**Proof:**

apple/Apps/SessionWorkbench.swift:19
```
    private var needsYou: [SessionEntry] {
        store.entries.filter { $0.session.question != nil }.sorted {
            ($0.session.question?["askedAt"]?.string ?? $0.session.updatedAt ?? "") < ($1.session.question?["askedAt"]?.string ?? $1.session.updatedAt ?? "")
        }
    }
```

apple/Apps/SessionWorkbench.swift:30
```
                return terms.allSatisfy { term in fields.contains { $0.localizedCaseInsensitiveContains(term) } }
            }
            return store.includesSelectedMachine(entry.host) && (!needsOnly || entry.session.question != nil)
        }
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'needsYou.*selectedMachine|includesSelectedMachine.*question' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
apple/Apps/SessionWorkbench.swift:32:            return store.includesSelectedMachine(entry.host) && (!needsOnly || entry.session.question != nil)
$ rg -n -F -- 'private var needsYou: [SessionEntry] {' apple/Apps/SessionWorkbench.swift
19:    private var needsYou: [SessionEntry] {
$ rg -n -F -- 'return terms.allSatisfy { term in fields.contains { $0.localizedCaseInsensitiveContains(term) } }' apple/Apps/SessionWorkbench.swift
30:                return terms.allSatisfy { term in fields.contains { $0.localizedCaseInsensitiveContains(term) } }
```

**Do:** Apply the selected-machine predicate to the attention list before sorting and counting it. Verify two hosts with pending questions and one selected machine.

**Why:** The ordinary list checks machine scope; the attention section only checks whether a question exists.

**Effort:** S · **Impact:** M

### FUNC-3. Search recently ended sessions

**Location:** `apple/Apps/SessionWorkbench.swift:26`, `apple/Apps/SessionWorkbench.swift:67`

**Proof:**

apple/Apps/SessionWorkbench.swift:26
```
        let terms = search.split(whereSeparator: \.isWhitespace).map(String.init)
        let matched = store.entries.filter { entry in
            if !terms.isEmpty {
                let fields = [entry.session.title, entry.session.agent, entry.session.repository ?? "", entry.session.branch ?? "", entry.session.cwd ?? "", entry.machineName]
                return terms.allSatisfy { term in fields.contains { $0.localizedCaseInsensitiveContains(term) } }
```

apple/Apps/SessionWorkbench.swift:67
```
                if search.isEmpty, !needsOnly, !store.endedEntries.isEmpty {
                    Section("Recently ended") {
                        ForEach(store.endedEntries.filter { store.includesSelectedMachine($0.host) }) { entry in row(entry).opacity(0.6) }
                    }
                }
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'endedEntries.*search|search.*endedEntries' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
apple/Apps/SessionWorkbench.swift:67:                if search.isEmpty, !needsOnly, !store.endedEntries.isEmpty {
$ rg -n -F -- 'let terms = search.split(whereSeparator: \.isWhitespace).map(String.init)' apple/Apps/SessionWorkbench.swift
26:        let terms = search.split(whereSeparator: \.isWhitespace).map(String.init)
$ rg -n -F -- 'if search.isEmpty, !needsOnly, !store.endedEntries.isEmpty {' apple/Apps/SessionWorkbench.swift
67:                if search.isEmpty, !needsOnly, !store.endedEntries.isEmpty {
```

**Do:** Match recently ended sessions with the same query fields as live sessions and label ended results. Keep their controls read-only.

**Why:** A user can see a finished session until they type its title; the search then excludes the entire ended section.

**Effort:** M · **Impact:** M

### FUNC-4. Paste images into iPhone and iPad replies

**Location:** `apple/Apps/AttachmentComposer.swift:47`

**Proof:**

apple/Apps/AttachmentComposer.swift:47
```
                Button("Attach File", systemImage: "paperclip") { importing = true }
                PhotosPicker(selection: $photo, matching: .images) { Label("Photo", systemImage: "photo") }
                #if os(macOS)
                    Button("Paste Image", systemImage: "clipboard") { pasteImage() }
                #endif
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'PasteButton|onPasteCommand|pasteDestination' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
# (no matches)
$ rg -n -F -- 'Button("Attach File", systemImage: "paperclip") { importing = true }' apple/Apps/AttachmentComposer.swift
47:                Button("Attach File", systemImage: "paperclip") { importing = true }
```

**Do:** Add an explicit iOS paste control (new) that feeds the same bounded attachment pipeline. Verify it with an iPad screenshot and with a host that does not accept attachments.

**Why:** File and Photos import are available on both platforms; image paste is compiled only on Mac.

**Effort:** M · **Impact:** M

### FUNC-5. Offer opt-in notifications while the iPad is asleep

**Location:** `apple/Apps/SettingsNotificationsView.swift:27`

**Proof:**

apple/Apps/SettingsNotificationsView.swift:27
```
            #if os(iOS)
                Section {
                    Text("Notifications are available while Agent Bridge is open. Background push notifications are not configured.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'registerForRemoteNotifications|didRegisterForRemoteNotifications|aps-environment' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
# (no matches)
$ rg -n -F -- '#if os(iOS)' apple/Apps/SettingsNotificationsView.swift
27:            #if os(iOS)
```

**Do:** Design an optional push delivery path (new) with generic payloads, revocable device registration and a signed build with the necessary entitlement. Keep foreground notifications working in local builds without a provider.

**Why:** The settings screen explicitly says notifications require the app to be open. A host waiting on an answer cannot currently alert an away user through this client path.

**Effort:** L · **Impact:** L

## Stability

Four survived. The suspected duplicate-delegate crash and unsafe usage-window identifiers are already guarded.

### STAB-1. Keep pinned sessions when the order history rolls over

**Location:** `apple/State/BridgeStore.swift:684`, `apple/State/BridgeStore.swift:695`

**Proof:**

apple/State/BridgeStore.swift:684
```
        let newSessions = sessions.filter { !manualOrder.contains(machineID + ":" + $0.id) }.sorted { ($0.updatedAt ?? "") > ($1.updatedAt ?? "") }
        guard !newSessions.isEmpty else { return }
        let newIDs = newSessions.map { machineID + ":" + $0.id }
        manualOrder = Array((newIDs + manualOrder).prefix(300))
```

apple/State/BridgeStore.swift:695
```
        readVersions = readVersions.filter { kept.contains($0.key) }
        pinned.formIntersection(kept)
        UserDefaults.standard.set(manualOrder, forKey: preferenceScope + ".order")
        UserDefaults.standard.set(readVersions, forKey: preferenceScope + ".read")
        UserDefaults.standard.set(Array(pinned), forKey: preferenceScope + ".pins")
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'pinned.*prefix|prefix.*pinned|pinned\.formIntersection' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
apple/State/BridgeStore.swift:696:        pinned.formIntersection(kept)
$ rg -n -F -- 'let newSessions = sessions.filter { !manualOrder.contains(machineID + ":" + $0.id) }.sorted { ($0.updatedAt ?? "") > ($1.updatedAt ?? "") }' apple/State/BridgeStore.swift
684:        let newSessions = sessions.filter { !manualOrder.contains(machineID + ":" + $0.id) }.sorted { ($0.updatedAt ?? "") > ($1.updatedAt ?? "") }
$ rg -n -F -- 'readVersions = readVersions.filter { kept.contains($0.key) }' apple/State/BridgeStore.swift
695:        readVersions = readVersions.filter { kept.contains($0.key) }
```

**Do:** Separate explicit pins from the bounded recent-order history. Exercise one pinned session followed by 300 newer session IDs from other machines.

**Why:** Registration caps the history and intersects the pin set with its survivors, silently removing an intentional pin.

**Effort:** M · **Impact:** M

### STAB-2. Keep the iPad inspector preference across session changes

**Location:** `apple/Apps/MobileSessionWorkspace.swift:13`, `apple/Apps/SessionWorkbench.swift:219`

**Proof:**

apple/Apps/MobileSessionWorkspace.swift:13
```
        @Environment(\.horizontalSizeClass) private var sizeClass
        @Environment(\.dynamicTypeSize) private var typeSize
        @State private var inspectorHidden = false

```

apple/Apps/SessionWorkbench.swift:219
```
            } inspector: {
                reader(entry, layout: .inspector)
            }
            .id(entry.id)
        #endif
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'inspectorHidden|SceneStorage.*[Ii]nspector' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
apple/Apps/MobileSessionWorkspace.swift:15:        @State private var inspectorHidden = false
apple/Apps/MobileSessionWorkspace.swift:20:                let sideBySide = canSplit && !inspectorHidden
apple/Apps/MobileSessionWorkspace.swift:41:                                inspectorHidden.toggle()
$ rg -n -F -- '@Environment(\.horizontalSizeClass) private var sizeClass' apple/Apps/MobileSessionWorkspace.swift
13:        @Environment(\.horizontalSizeClass) private var sizeClass
$ rg -n -F -- '} inspector: {' apple/Apps/SessionWorkbench.swift
219:            } inspector: {
```

**Do:** Store the wide-layout inspector choice at window scope (new state) while keeping the narrow-layout sheet transient. Switch between two sessions after hiding the pane.

**Why:** The hidden flag belongs to a view whose identity changes with the session; switching selection recreates its default.

**Effort:** S · **Impact:** M

### STAB-3. Treat whitespace-only sidebar searches as empty

**Location:** `apple/Apps/SessionWorkbench.swift:25`, `apple/Apps/SessionWorkbench.swift:61`, `apple/Apps/SessionWorkbench.swift:74`

**Proof:**

apple/Apps/SessionWorkbench.swift:25
```
    private var filtered: [SessionEntry] {
        let terms = search.split(whereSeparator: \.isWhitespace).map(String.init)
        let matched = store.entries.filter { entry in
            if !terms.isEmpty {
```

apple/Apps/SessionWorkbench.swift:61
```
                if search.isEmpty, !needsOnly, !needsYou.isEmpty {
                    Section("Needs you · \(needsYou.count)") { ForEach(needsYou) { entry in row(entry) } }
                }
                Section(needsOnly ? "Needs you" : search.isEmpty ? "Sessions" : "Search results") {
                    ForEach(filtered) { entry in row(entry) }
```

apple/Apps/SessionWorkbench.swift:74
```
                if store.entries.isEmpty, store.endedEntries.isEmpty {
                    emptyState
                } else if filtered.isEmpty, !search.isEmpty {
                    ContentUnavailableView.search(text: search)
                }
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'search\.trimmingCharacters|terms\.isEmpty' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
apple/Apps/SessionWorkbench.swift:28:            if !terms.isEmpty {
apple/Apps/SessionWorkbench.swift:34:        if !terms.isEmpty {
apple/Apps/SessionWorkbench.swift:35:            let query = search.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
$ rg -n -F -- 'private var filtered: [SessionEntry] {' apple/Apps/SessionWorkbench.swift
25:    private var filtered: [SessionEntry] {
$ rg -n -F -- 'if search.isEmpty, !needsOnly, !needsYou.isEmpty {' apple/Apps/SessionWorkbench.swift
61:                if search.isEmpty, !needsOnly, !needsYou.isEmpty {
$ rg -n -F -- 'if store.entries.isEmpty, store.endedEntries.isEmpty {' apple/Apps/SessionWorkbench.swift
74:                if store.entries.isEmpty, store.endedEntries.isEmpty {
```

**Do:** Derive one effective search state from the trimmed terms. Use it for filtering, section visibility and the empty-results overlay; cover spaces and pasted newlines.

**Why:** Filtering tokenizes whitespace, while the sidebar checks raw string emptiness. A space can hide sections even though there is no query.

**Effort:** S · **Impact:** S

### STAB-4. Avoid silently switching sort mode on a no-op move

**Location:** `apple/State/BridgeStore.swift:702`, `apple/State/BridgeStore.swift:706`

**Proof:**

apple/State/BridgeStore.swift:702
```
    func move(_ id: String, by distance: Int) {
        var order = sorted(entries).map(\.id)
        guard let index = order.firstIndex(of: id) else { return }
        let target = min(max(0, index + distance), order.count - 1)
        order.remove(at: index)
```

apple/State/BridgeStore.swift:706
```
        order.remove(at: index)
        order.insert(id, at: target)
        manualOrder = order
        sortOrder = "manual"
        UserDefaults.standard.set(order, forKey: preferenceScope + ".order")
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'target != index|target == index|index != target' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
# (no matches)
$ rg -n -F -- 'func move(_ id: String, by distance: Int) {' apple/State/BridgeStore.swift
702:    func move(_ id: String, by distance: Int) {
$ rg -n -F -- 'order.remove(at: index)' apple/State/BridgeStore.swift
706:        order.remove(at: index)
```

**Do:** Return before rewriting the order or sort preference when the requested destination equals the current index. Exercise “Move Up” on the first row in most-recent order.

**Why:** Clamping an out-of-range move leaves the row in place but still changes ordering to manual.

**Effort:** S · **Impact:** S

## Reliability

### REL-1. Verify one iPad reply all the way to a Mac agent

**Location:** `docs/apple-parity.md:102`, `docs/apple-parity.md:107`

**Proof:**

docs/apple-parity.md:102
```
authorization. The signed app showed two connected machines and two live sessions;
selecting one showed the conversation, an editable reply with a staged image,
and the session inspector. This verifies physical sign-in, discovery, session
data and composer presentation. It does not establish delivery of that draft.
```

docs/apple-parity.md:107
```
Still pending: the remaining real .NET-host/relay interoperability cases covering
multi-machine reconnect, answer/reply/stop receipts, attachment delivery, launch,
folder trust, copy/resume and remote update requests; OS notification
authorization/delivery and Mac login registration; physical iPhone and minimum-supported-OS devices; and VoiceOver
plus large-text interaction testing. These are validation gaps for implemented
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'physical iPad|physical.*receipt|physical.*deliver|Shiva III' apple/README.md docs/apple-parity.md docs/apple-quality-pass.md apple/Tests
docs/apple-parity.md:100:On a physical iPad, switching to Safari for device authorization exposed a lost
apple/README.md:95:simulators. A signed physical iPad completed GitHub authorization and displayed
$ rg -n -F -- 'authorization. The signed app showed two connected machines and two live sessions;' docs/apple-parity.md
102:authorization. The signed app showed two connected machines and two live sessions;
$ rg -n -F -- 'Still pending: the remaining real .NET-host/relay interoperability cases covering' docs/apple-parity.md
107:Still pending: the remaining real .NET-host/relay interoperability cases covering
```

**Do:** Run a repeatable live acceptance case (new) in a disposable approved Mac workspace: launch a managed session, send a unique harmless reply from the physical iPad, observe it exactly once in the agent, and match the client receipt to the host outcome. Repeat with an interrupted receipt, retaining the original request identity and checking for duplicate input.

**Why:** The recorded device run reached the editable composer and inspector. It expressly stops before transmission; rendered UI and fixture receipts do not prove live delivery.

**Effort:** M · **Impact:** L

### REL-2. Make account-save failure recoverable after browser authorization

**Location:** `apple/State/BridgeStore.swift:301`, `apple/State/BridgeStore.swift:310`

**Proof:**

apple/State/BridgeStore.swift:301
```
                guard token == signInGeneration else { return }
                try BridgeKeychain.save(JSONEncoder().encode(signedIn), name: "account:\(address)")
                relayAddress = address
                account = signedIn
                isSigningIn = false
```

apple/State/BridgeStore.swift:310
```
                guard token == signInGeneration else { return }
                isSigningIn = false
                signInCode = nil
                signInStatus = error.localizedDescription
            }
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'pendingAccount|pendingCredential|retry.*[Kk]eychain|[Kk]eychain.*retry' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
# (no matches)
$ rg -n -F -- 'guard token == signInGeneration else { return }' apple/State/BridgeStore.swift
288:                guard token == signInGeneration else { return }
301:                guard token == signInGeneration else { return }
310:                guard token == signInGeneration else { return }
```

**Do:** Retain the just-issued account in process memory briefly after a recoverable secure-storage error and offer a bounded retry (new). Clear it on cancellation, expiry or account change; do not persist it outside secure storage.

**Why:** If saving fails after GitHub approval, the catch clears sign-in and the code; a browser success can lead straight back to the sign-in button. This is a failure path, not a diagnosis of Shiva III.

**Effort:** M · **Impact:** L

### REL-3. Distinguish temporary relay HTTP failures from expired grants

**Location:** `apple/Sources/AgentBridgeAppleCore/RelaySignIn.swift:147`, `apple/Sources/AgentBridgeAppleCore/RelaySignIn.swift:154`

**Proof:**

apple/Sources/AgentBridgeAppleCore/RelaySignIn.swift:147
```
                if let json = try? JSONDecoder().decode(JSONValue.self, from: data),
                   let next = json["interval"]?.number, next.isFinite, next >= 1, next <= 60
                {
                    interval = max(interval, Int(next))
                }
```

apple/Sources/AgentBridgeAppleCore/RelaySignIn.swift:154
```
            guard response.statusCode == 200 else {
                // Do not reflect arbitrary server error text that might contain a token.
                throw RelayError.refused("Sign-in was refused or the code expired. Start sign-in again.")
            }
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'statusCode == 503|Retry-After|statusCode == 429' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
# (no matches)
$ rg -n -F -- 'if let json = try? JSONDecoder().decode(JSONValue.self, from: data),' apple/Sources/AgentBridgeAppleCore/RelaySignIn.swift
147:                if let json = try? JSONDecoder().decode(JSONValue.self, from: data),
$ rg -n -F -- 'guard response.statusCode == 200 else {' apple/Sources/AgentBridgeAppleCore/RelaySignIn.swift
92:        guard response.statusCode == 200 else { throw RelayError.refused("Sign-in is unavailable (\(response.statusCode)).") }
154:            guard response.statusCode == 200 else {
```

**Do:** Honor a bounded backoff for retryable poll responses where the relay confirms it has not consumed the grant. Keep denial and expiry terminal; test a temporary failure followed by authorization without replaying an ambiguous token exchange.

**Why:** The poll retries transient transport errors but maps all non-200, non-202 HTTP responses to refusal or expiry.

**Effort:** M · **Impact:** L

### REL-4. Preserve an explicit disconnect across foregrounding

**Location:** `apple/State/BridgeStore.swift:261`, `apple/State/BridgeStore.swift:270`, `apple/Apps/ConnectionView.swift:35`

**Proof:**

apple/State/BridgeStore.swift:261
```
        canPollGitHub = false
        notifications.setForeground(false)
        if !isSample, !isFixture {
            disconnect()
        }
```

apple/State/BridgeStore.swift:270
```
        notifications.setForeground(true)
        if !isSample, !isFixture {
            connect()
        }
    }
```

apple/Apps/ConnectionView.swift:35
```
                    }
                    Button("Reconnect", action: connect).disabled(store.isSigningIn || store.isFixture)
                    if store.isLive || store.isConnecting {
                        Button("Disconnect", action: store.disconnect).disabled(store.isFixture)
                    }
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'userDisconnected|manualDisconnect|autoReconnectEnabled|disconnectIntent' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
# (no matches)
$ rg -n -F -- 'canPollGitHub = false' apple/State/BridgeStore.swift
261:        canPollGitHub = false
$ rg -n -F -- 'notifications.setForeground(true)' apple/State/BridgeStore.swift
270:        notifications.setForeground(true)
$ rg -n -F -- 'Button("Reconnect", action: connect).disabled(store.isSigningIn || store.isFixture)' apple/Apps/ConnectionView.swift
36:                    Button("Reconnect", action: connect).disabled(store.isSigningIn || store.isFixture)
```

**Do:** Distinguish an explicit disconnect from suspension (new state). Restore connections after a system suspension only if the user had left them enabled.

**Why:** The foreground handler calls connect for every non-fixture, non-sample session, even after the user explicitly disconnected.

**Effort:** S · **Impact:** M

### REL-5. Report when attachment cleanup was not confirmed

**Location:** `apple/Sources/AgentBridgeAppleCore/BridgeBlobStaging.swift:49`

**Proof:**

apple/Sources/AgentBridgeAppleCore/BridgeBlobStaging.swift:49
```
                    _ = try await send(.init(op: "attachment.discard", data: ["attachment": .string(attachment.id.uuidString.lowercased())]), .seconds(5))
                } catch {}
                try? await deleteBlob()
            }.value
            throw error
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'cleanupFailed|cleanupPending|discardFailed' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
# (no matches)
$ rg -n -F -- '_ = try await send(.init(op: "attachment.discard", data: ["attachment": .string(attachment.id.uuidString.lowercased())]), .seconds(5))' apple/Sources/AgentBridgeAppleCore/BridgeBlobStaging.swift
49:                    _ = try await send(.init(op: "attachment.discard", data: ["attachment": .string(attachment.id.uuidString.lowercased())]), .seconds(5))
```

**Do:** Record a bounded, payload-free cleanup diagnostic (new) if discarding or deleting fails. Preserve the original upload failure and never send cleanup to a replacement peer.

**Why:** Both cleanup errors are discarded, so an operator cannot distinguish a clean rollback from a peer that may retain staged data until expiry.

**Effort:** S · **Impact:** M

## Security

Four survived. Redirect refusal, notification preview defaults and signed-command timestamp bounds already address three other candidates.

### SEC-1. Confirm cleanup of private tmux input buffers

**Location:** `src/AgentBridge.Host.Delivery/Mac/ManagedTmuxBackend.cs:242`

**Proof:**

src/AgentBridge.Host.Delivery/Mac/ManagedTmuxBackend.cs:242
```
                        // -d removes a successfully pasted buffer. This also removes one
                        // left by cancellation, a changed prompt or an uncertain paste.
                        try { await commands.RunAsync(["delete-buffer", "-b", buffer]).ConfigureAwait(false); }
                        catch (Exception) { }
                    }
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'delete-buffer|list-buffers' src/AgentBridge.Host.Delivery/Mac
src/AgentBridge.Host.Delivery/Mac/ManagedTmuxBackend.cs:244:                        try { await commands.RunAsync(["delete-buffer", "-b", buffer]).ConfigureAwait(false); }
$ rg -n -F -- '// -d removes a successfully pasted buffer. This also removes one' src/AgentBridge.Host.Delivery/Mac/ManagedTmuxBackend.cs
242:                        // -d removes a successfully pasted buffer. This also removes one
```

**Do:** Distinguish an already-consumed buffer from a failed delete. Track uncertain cleanup for a bounded retry on the same verified tmux server, using only the buffer identifier and never logging its contents.

**Why:** Private reply text may remain in the managed server if both paste and cleanup fail. Current cleanup ignores exit status and exceptions.

**Effort:** M · **Impact:** M

### SEC-2. Restrict device verification to the GitHub device page

**Location:** `apple/Sources/AgentBridgeAppleCore/RelaySignIn.swift:95`, `apple/Apps/ConnectionView.swift:52`

**Proof:**

apple/Sources/AgentBridgeAppleCore/RelaySignIn.swift:95
```
              code.expiresIn <= 3600, code.interval > 0, code.interval <= 60,
              code.verificationUri.scheme == "https", code.verificationUri.host == "github.com",
              code.verificationUri.user == nil, code.verificationUri.password == nil
        else { throw RelayError.invalidMessage }
        return code
```

apple/Apps/ConnectionView.swift:52
```
                            Button("Copy Code and Open GitHub") {
                                copy(code.userCode)
                                copiedCode = true
                                openURL(code.verificationUri)
                            }
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'verificationUri\.path|verificationUri\.port|github\.com/login/device' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
# (no matches)
$ rg -n -F -- 'code.expiresIn <= 3600, code.interval > 0, code.interval <= 60,' apple/Sources/AgentBridgeAppleCore/RelaySignIn.swift
95:              code.expiresIn <= 3600, code.interval > 0, code.interval <= 60,
$ rg -n -F -- 'Button("Copy Code and Open GitHub") {' apple/Apps/ConnectionView.swift
52:                            Button("Copy Code and Open GitHub") {
```

**Do:** Validate the canonical device-login URL before offering the browser action. Reject alternate paths, ports and unexpected query or fragment content; retain tests for scheme, credentials and host.

**Why:** The response is relay-supplied. Checking github.com alone permits unrelated pages on that domain, even though the UI presents the button as device authorization.

**Effort:** S · **Impact:** M

### SEC-3. Give copied Mac device codes a bounded lifetime

**Location:** `apple/Apps/ConnectionView.swift:152`

**Proof:**

apple/Apps/ConnectionView.swift:152
```
        #if os(macOS)
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
        #else
            UIPasteboard.general.setItems([["public.utf8-plain-text": text]], options: [.localOnly: true, .expirationDate: Date().addingTimeInterval(TimeInterval(store.signInCode?.expiresIn ?? 300))])
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'changeCount|concealed|expirationDate|localOnly' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
apple/Apps/ConnectionView.swift:156:            UIPasteboard.general.setItems([["public.utf8-plain-text": text]], options: [.localOnly: true, .expirationDate: Date().addingTimeInterval(TimeInterval(store.signInCode?.expiresIn ?? 300))])
$ rg -n -F -- '#if os(macOS)' apple/Apps/ConnectionView.swift
2:#if os(macOS)
80:                    #if os(macOS)
96:                #if os(macOS)
130:        #if os(macOS)
152:        #if os(macOS)
```

**Do:** Mark the Mac item as sensitive and clear it at expiry only if the clipboard still contains the item this app wrote. Verify that copying unrelated text afterward is never undone.

**Why:** The Mac branch writes a plain string to the general pasteboard; the iOS branch explicitly restricts transfer and lifetime.

**Effort:** S · **Impact:** M

### SEC-4. Make links in host-supplied Markdown disclose their destination

**Location:** `apple/Apps/BridgeMarkdown.swift:103`

**Proof:**

apple/Apps/BridgeMarkdown.swift:103
```
    // A host may supply text, but it cannot turn a file or script URL into a native action.
    for run in value.runs {
        if let link = run.link, !["https", "http", "mailto"].contains(link.scheme?.lowercased() ?? "") {
            value[run.range].link = nil
        }
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'openURL|confirmationDialog|linkDestination|allow.*[Hh]ost' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
apple/Apps/MachinesView.swift:107:            .confirmationDialog("Forget this machine?", isPresented: Binding(get: { forgetRequest != nil }, set: {
apple/Apps/MachinesView.swift:316:        .confirmationDialog("Install update on \(host.name)?", isPresented: Binding(get: { pendingInstall != nil }, set: {
apple/Apps/ConnectionView.swift:11:    @Environment(\.openURL) private var openURL
apple/Apps/ConnectionView.swift:55:                                openURL(code.verificationUri)
$ rg -n -F -- '// A host may supply text, but it cannot turn a file or script URL into a native action.' apple/Apps/BridgeMarkdown.swift
103:    // A host may supply text, but it cannot turn a file or script URL into a native action.
```

**Do:** Before opening an external destination (new confirmation), display the effective host or mail recipient when the visible link text conceals it. Preserve copy and keyboard activation.

**Why:** The scheme allowlist blocks script and file actions, but allows a host to label an arbitrary web or mail destination as something trusted.

**Effort:** M · **Impact:** M

## Usability

### USE-1. Show the failing stage of sign-in where the user returns

**Location:** `apple/Apps/ConnectionView.swift:66`, `apple/State/BridgeStore.swift:310`

**Proof:**

apple/Apps/ConnectionView.swift:66
```
                        }
                    }
                    if let status = store.signInStatus {
                        Text(status).font(.callout).foregroundStyle(.secondary)
                    }
```

apple/State/BridgeStore.swift:310
```
                guard token == signInGeneration else { return }
                isSigningIn = false
                signInCode = nil
                signInStatus = error.localizedDescription
            }
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'signInStage|credentialStorageStatus|signInRecovery' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
# (no matches)
$ rg -n -F -- 'if let status = store.signInStatus {' apple/Apps/ConnectionView.swift
68:                    if let status = store.signInStatus {
$ rg -n -F -- 'guard token == signInGeneration else { return }' apple/State/BridgeStore.swift
288:                guard token == signInGeneration else { return }
301:                guard token == signInGeneration else { return }
310:                guard token == signInGeneration else { return }
```

**Do:** Render the last failed stage and a recovery action beside the account status (new). Distinguish browser approval, saving on this device, and connecting to the relay; avoid showing secrets.

**Why:** After browser approval, a generic error beside a fresh sign-in button does not tell the user which part succeeded or whether retrying GitHub would help.

**Effort:** M · **Impact:** L

### USE-2. Show the local profile in Mac connection recovery

**Location:** `apple/Sources/AgentBridgeAppleCore/LocalBridge.swift:13`, `apple/Apps/ConnectionView.swift:79`

**Proof:**

apple/Sources/AgentBridgeAppleCore/LocalBridge.swift:13
```
                switch self {
                case .disconnected: "The local host disconnected."
                case .timeout: "The local host did not respond in time."
                case .duplicateID: "A request identifier was reused."
                case .invalidMessage: "The local host sent an invalid message."
```

apple/Apps/ConnectionView.swift:79
```
                    TextField("This device’s name", text: $deviceName)
                    #if os(macOS)
                        TextField("Local host profile", text: $profile).autocorrectionDisabled()
                    #endif
                    Button("Apply and Reconnect", action: connect)
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'timed out.*profile|profile.*timed out|did not respond.*profile|check.*[Hh]ost profile' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
# (no matches)
$ rg -n -F -- 'switch self {' apple/Sources/AgentBridgeAppleCore/LocalBridge.swift
13:                switch self {
$ rg -n -F -- 'TextField("This device’s name", text: $deviceName)' apple/Apps/ConnectionView.swift
79:                    TextField("This device’s name", text: $deviceName)
```

**Do:** When the Mac local connection times out, show the selected profile with an action (new) to review it and retry. Keep the explanation separate from GitHub sign-in, since local profile selection and relay authentication are independent.

**Why:** The timeout message names neither the profile nor the next check. The profile is editable elsewhere in settings, which makes a mismatch easy to miss.

**Effort:** S · **Impact:** M

### USE-3. Explain a filtered list with no sessions

**Location:** `apple/Apps/SessionWorkbench.swift:75`

**Proof:**

apple/Apps/SessionWorkbench.swift:75
```
                    emptyState
                } else if filtered.isEmpty, !search.isEmpty {
                    ContentUnavailableView.search(text: search)
                }
            }
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'filtered\.isEmpty|Clear [Ff]ilters|No sessions on' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
apple/Apps/SessionWorkbench.swift:76:                } else if filtered.isEmpty, !search.isEmpty {
$ rg -n -F -- emptyState apple/Apps/SessionWorkbench.swift
75:                    emptyState
265:    private var emptyState: some View {
```

**Do:** Add an empty result for a machine or attention filter (new) with a reset action. Preserve the separate text-search and no-host states.

**Why:** The overlay only handles a globally empty store or an empty text search; filtering a populated store to zero rows otherwise leaves no explanation.

**Effort:** S · **Impact:** M

### USE-4. Show how many attention items the Mac menu omits

**Location:** `apple/Apps/AgentBridgeApp.swift:49`

**Proof:**

apple/Apps/AgentBridgeApp.swift:49
```
                    openWindow(id: "workbench")
                    NSApp.activate(ignoringOtherApps: true)
                }
            }
            Button("Open Agent Bridge") { openWindow(id: "workbench"); NSApp.activate(ignoringOtherApps: true) }
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'prefix\(8\)|more questions|more pending' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
apple/Apps/AgentBridgeApp.swift:46:            ForEach(Array(store.entries.filter { $0.session.question != nil }.prefix(8))) { entry in
apple/Sources/AgentBridgeAppleCore/BridgeDelegateLabel.swift:13:            }.prefix(8)
apple/Sources/AgentBridgeAppleCore/LocalEndpoint.swift:26:            let hash = SHA256.hash(data: Data(folder.utf8)).prefix(8).map { String(format: "%02x", $0) }.joined()
$ rg -n -F -- 'openWindow(id: "workbench")' apple/Apps/AgentBridgeApp.swift
49:                    openWindow(id: "workbench")
53:            Button("Open Agent Bridge") { openWindow(id: "workbench"); NSApp.activate(ignoringOtherApps: true) }
```

**Do:** When more than eight questions are waiting, show the hidden count and an action (new) that opens the full attention list. Keep the machine names on the eight visible items.

**Why:** The menu caps actionable titles at eight; the generic open command does not indicate how many were omitted.

**Effort:** S · **Impact:** M

### USE-5. Show the keyboard shortcut on the reply composer

**Location:** `apple/Apps/SessionActions.swift:177`

**Proof:**

apple/Apps/SessionActions.swift:177
```
                           disabled: replyHold != nil || attempt?.isSending == true || attempt?.uncertain == true) { uploading = $0 }
        Button("Send Reply", systemImage: "paperplane", action: sendReply)
            .keyboardShortcut(.return, modifiers: .command)
            .buttonStyle(.borderedProminent)
            .disabled(busy || attempt?.uncertain == true || replyHold != nil || replyText.wrappedValue.isEmpty && attachments.wrappedValue.isEmpty)
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- '⌘.*(Return|Enter)|Command.Return|help\(.*[Rr]eply|accessibilityHint.*[Ss]end' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
# (no matches)
$ rg -n -F -- 'disabled: replyHold != nil || attempt?.isSending == true || attempt?.uncertain == true) { uploading = $0 }' apple/Apps/SessionActions.swift
177:                           disabled: replyHold != nil || attempt?.isSending == true || attempt?.uncertain == true) { uploading = $0 }
```

**Do:** Add a visible shortcut hint (new) on Mac and when a hardware keyboard is available on iPad. Keep the multiline field behavior and verify the hint matches the actual shortcut.

**Why:** Command-Return is wired to Send Reply, but the button title does not tell keyboard users how to send without leaving the field.

**Effort:** S · **Impact:** S

## Attractiveness / Sexiness

Four survived. The iPad already adapts to narrow widths and large text, and the inline inspector already has a labeled header. Appearance recommendations are design judgments, not measured contrast failures.

### ATTR-1. Separate the conversation visually from the inspector

**Location:** `apple/Apps/SessionDetail.swift:64`, `apple/Apps/SessionDetail.swift:83`

**Proof:**

apple/Apps/SessionDetail.swift:64
```
                if let message = session.activity?["userMessage"]?.string, !message.isEmpty {
                    Section("You") { Text(message) }
                }
                if session.response?.isEmpty == false || session.responseTruncated {
                    Section("\(session.agent.capitalized) response") {
```

apple/Apps/SessionDetail.swift:83
```
                Section("Details") {
                    ForEach(SessionPresentation.details(session: session, fullSession: fullSession, machineName: machineName)) { detail in
                        LabeledContent(detail.label) {
                            Text(detail.value)
                                .fontDesign(detail.monospaced ? .monospaced : .default)
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'chatBubble|conversationSurface|messageSurface|transcriptStyle' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
# (no matches)
$ rg -n -F -- 'if let message = session.activity?["userMessage"]?.string, !message.isEmpty {' apple/Apps/SessionDetail.swift
64:                if let message = session.activity?["userMessage"]?.string, !message.isEmpty {
$ rg -n -F -- 'Section("Details") {' apple/Apps/SessionDetail.swift
83:                Section("Details") {
```

**Do:** Give user prompts and responses distinct spacing and readable text widths (new conversation treatment). Retain compact key/value rows for machine details, and verify the composer stays anchored.

**Why:** Both the prompt and response render as form sections in the same idiom as inspector metadata. The session is difficult to scan as an exchange.

**Effort:** M · **Impact:** M

### ATTR-2. Give distinct connection states distinct visual cues

**Location:** `apple/Apps/SessionWorkbench.swift:265`

**Proof:**

apple/Apps/SessionWorkbench.swift:265
```
    private var emptyState: some View {
        ContentUnavailableView {
            Label(store.isConnecting ? "Connecting" : store.isLive ? "No active sessions" : "Connect to Agent Bridge", systemImage: "terminal")
        } description: {
            if store.account == nil, !store.isSample, let status = store.signInStatus {
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'systemImage:.*(antenna|bolt|network)|connectingIcon' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
apple/Apps/SessionWorkbench.swift:101:                            Button("Connection Settings", systemImage: "network") { showingConnection = true }
apple/Apps/SessionWorkbench.swift:300:            Label(store.connectionStatus, systemImage: store.isLive ? "checkmark.circle" : "network")
apple/Apps/SessionActions.swift:88:                        Label(host.status, systemImage: "network.slash")
$ rg -n -F -- 'private var emptyState: some View {' apple/Apps/SessionWorkbench.swift
265:    private var emptyState: some View {
```

**Do:** Use distinct symbols and a restrained activity indicator (new) for connecting, connected-but-empty, and signed-out states. Pair them with the existing text and respect Reduce Motion.

**Why:** All three states use the same terminal glyph; a transient connection attempt looks much like a stable empty workspace.

**Effort:** S · **Impact:** M

### ATTR-3. Keep ended rows readable while marking them as history

**Location:** `apple/Apps/SessionWorkbench.swift:67`

**Proof:**

apple/Apps/SessionWorkbench.swift:67
```
                if search.isEmpty, !needsOnly, !store.endedEntries.isEmpty {
                    Section("Recently ended") {
                        ForEach(store.endedEntries.filter { store.includesSelectedMachine($0.host) }) { entry in row(entry).opacity(0.6) }
                    }
                }
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'opacity\(0\.6\)|ended.*foregroundStyle' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
apple/Apps/SessionWorkbench.swift:69:                        ForEach(store.endedEntries.filter { store.includesSelectedMachine($0.host) }) { entry in row(entry).opacity(0.6) }
$ rg -n -F -- 'if search.isEmpty, !needsOnly, !store.endedEntries.isEmpty {' apple/Apps/SessionWorkbench.swift
67:                if search.isEmpty, !needsOnly, !store.endedEntries.isEmpty {
```

**Do:** Tone down the ended status and symbol rather than the entire row. Check title legibility in light, dark and increased-contrast settings.

**Why:** Opacity is applied to the entire row, including secondary metadata; every part of a selectable historical item loses prominence together.

**Effort:** S · **Impact:** M

### ATTR-4. Give the unread indicator a defined visual role

**Location:** `apple/Apps/SessionWorkbench.swift:155`

**Proof:**

apple/Apps/SessionWorkbench.swift:155
```
                    }
                    if store.isUnread(entry) {
                        Circle().frame(width: 6, height: 6).accessibilityLabel("Unread")
                    }
                    Text(entry.machineName)
```

**Verified:** Competing implementations and cited sites (commands run from the repository root).

```console
$ rg -n -- 'Unread.*foregroundStyle|unreadColor|unreadBadge|Unread.*tint' apple/Apps apple/State apple/Sources/AgentBridgeAppleCore
# (no matches)
$ rg -n -F -- 'if store.isUnread(entry) {' apple/Apps/SessionWorkbench.swift
156:                    if store.isUnread(entry) {
```

**Do:** Give the unread dot an explicit accent treatment (new) and align it consistently with machine metadata. Preserve its accessibility label and check selected-row contrast.

**Why:** The six-point circle inherits the row foreground just like nearby pin and text, so unread and ordinary metadata compete visually.

**Effort:** S · **Impact:** S

## Dropped during verification

- Missing iPad composer or inspector — both are present, with a sheet fallback and accessible-size override (apple/Apps/MobileSessionWorkspace.swift:19–57); physical acceptance is recorded separately.
- Unbounded delegate nesting or duplicate-key crash — apple/Sources/AgentBridgeAppleCore/BridgeDelegatePresentation.swift:26–36 and :52–67 deduplicate input and traverse with a visited set.
- Missing generic device-code network retry — apple/Sources/AgentBridgeAppleCore/RelaySignIn.swift:124–135 backs off within the grant; this differs from the HTTP status case retained above.
- Missing TLS redirect defense or authentication response size bound — apple/Sources/AgentBridgeAppleCore/RelaySignIn.swift:72–76 and :178–184 use redirect refusal and cap the input.
- Unbounded tmux subprocess wait — src/AgentBridge.Host.Delivery/Mac/TmuxCommand.cs:29–36 links a timeout to input, output and exit.
- Duplicate usage-window view identities — apple/Sources/AgentBridgeAppleCore/SessionPresentation.swift:209–213 deduplicates keys before rendering.
- First-run notification preview leak — apple/State/BridgeNotificationManager.swift:61 reads a Boolean preference that defaults to false; the planner also defaults previews off.
- Missing screen-reader label for the reply field — apple/Apps/SessionActions.swift:168–172 labels it explicitly.
- Unsafely converting the command timestamp — apple/Sources/AgentBridgeAppleCore/BridgeSignedCommand.swift:35–38 checks finiteness and bounds before converting.
- Missing cleanup on upload failure — apple/Sources/AgentBridgeAppleCore/BridgeBlobStaging.swift:43–53 performs bounded independent cleanup. Only the inability to tell that cleanup failed survives.
- Missing inspector header or missing adaptive breakpoint — apple/Apps/MobileSessionWorkspace.swift:19 and :81–86 implement both. Do not add duplicate UI.

## First move

**REL-1. Verify one iPad reply all the way to a Mac agent.** On a disposable session, compare one harmless reply on Shiva III with what actually arrives at the Mac process and with the receipt shown to the sender. This establishes whether the bridge can do its central job before spending effort on presentation or repeating sign-in. A failure also gives the next PR a specific boundary to fix: request dispatch, host delivery, or receipt handling.
