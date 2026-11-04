import AgentBridgeAppleCore
import SwiftUI

struct MachinesView: View {
    @Bindable var store: BridgeStore
    @Environment(\.dismiss) private var dismiss
    @State private var usageHost: HostStore?
    @State private var showingAccountUsage = false
    @State private var forgetRequest: ForgetRequest?
    @State private var forgetTask: Task<Void, Never>?
    @State private var forgetError: String?

    private struct ForgetRequest {
        let machine: RelayMachine
        let displayedName: String
        let relayAddress: String
        let accountID: String?
    }

    private var displayedHosts: [HostStore] {
        store.isSample ? [] : store.canonicalHosts
    }

    private func remoteConnection(for host: HostStore) -> HostStore? {
        guard host.isLocal, let id = host.hello?["machine"]?["id"]?.string else { return nil }
        return store.hosts.first { !$0.isLocal && $0.hello?["machine"]?["id"]?.string == id }
    }

    private var knownWithoutHost: [RelayMachine] {
        guard !store.isSample else { return [] }
        let knownIDs = Set(store.hosts.flatMap { [$0.id, $0.hello?["machine"]?["id"]?.string].compactMap(\.self) })
        return store.roster.filter { !knownIDs.contains($0.id) }
    }

    private var updateTargets: [HostStore] {
        displayedHosts.map { remoteConnection(for: $0) ?? $0 }.filter(\.canCheckForUpdates)
    }

    var body: some View {
        NavigationStack {
            List {
                Section {
                    Text(store.connectionStatus).foregroundStyle(.secondary)
                    if !store.isSample, !store.isFixture, store.connectionPresentation.remote != store.connectionStatus {
                        Text(store.connectionPresentation.remote).foregroundStyle(.secondary)
                    }
                    if store.cachedUsage != nil {
                        Button("Usage Across Machines", systemImage: "chart.bar") { showingAccountUsage = true }
                    }
                    Button("Check All for Updates", systemImage: "arrow.clockwise") {
                        for host in updateTargets {
                            let connection = host.connectionID
                            let instance = host.instance
                            Task { @MainActor in
                                guard connection == host.connectionID, instance == host.instance, host.canCheckForUpdates else { return }
                                let request = UUID()
                                await host.perform(BridgeOperation(op: "update.check", data: ["request": .string(request.uuidString.lowercased())], requestID: request), slot: "update")
                            }
                        }
                    }.disabled(updateTargets.isEmpty)
                    if store.isSample {
                        Text("Sample sessions have no connected machine.")
                    } else if store.hosts.isEmpty, knownWithoutHost.isEmpty {
                        ContentUnavailableView("No machines connected", systemImage: "desktopcomputer",
                                               description: Text("Connect a local host or sign in from Connection settings."))
                    }
                    if let forgetError {
                        Text(forgetError).foregroundStyle(.red)
                    }
                    if forgetTask != nil {
                        ProgressView("Forgetting machine…")
                    }
                }
                ForEach(displayedHosts) { host in
                    Section {
                        MachineView(host: host, updateHost: remoteConnection(for: host), onUsage: { usageHost = host }) {
                            store.selectedMachine = host.id
                            dismiss()
                        }
                        if !host.isLocal, let machine = store.roster.first(where: { $0.id == host.id && !$0.online }) {
                            Button("Forget", role: .destructive) { prepareForget(machine, displayedName: host.name) }
                                .disabled(forgetTask != nil)
                        }
                    }
                }
                ForEach(knownWithoutHost) { machine in
                    Section {
                        VStack(alignment: .leading, spacing: 6) {
                            HStack {
                                Label(machine.name, systemImage: "desktopcomputer").font(.headline)
                                Spacer()
                                Text(machine.online ? "Connecting" : "Offline").foregroundStyle(.secondary)
                            }
                            if let lastSeen = SessionPresentation.date(machine.lastSeen) {
                                Text("Last seen \(SessionPresentation.timestamp(lastSeen))")
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                        }
                        .accessibilityElement(children: .combine)
                        if !machine.online {
                            Button("Forget", role: .destructive) { prepareForget(machine, displayedName: machine.name) }
                                .disabled(forgetTask != nil)
                        }
                    }
                }
            }
            .navigationTitle("Machines")
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() }.disabled(forgetTask != nil) } }
            .interactiveDismissDisabled(forgetTask != nil)
            .confirmationDialog("Forget this machine?", isPresented: Binding(get: { forgetRequest != nil }, set: {
                if !$0 {
                    forgetRequest = nil
                }
            }), presenting: forgetRequest) { request in
                Button("Forget \(request.displayedName)", role: .destructive) { forget(request) }
                Button("Cancel", role: .cancel) { forgetRequest = nil }
            } message: { request in
                Text("Remove “\(request.displayedName)” from remembered machines? It can appear again when it reconnects.")
            }
            .sheet(item: $usageHost) { host in
                NavigationStack {
                    UsageView(report: host.usage, machineName: host.name, status: host.status,
                              onRefresh: usageRefresh(for: host))
                        .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { usageHost = nil } } }
                }
                #if os(macOS)
                .frame(minWidth: 440, idealWidth: 560, minHeight: 480, idealHeight: 640)
                #endif
            }
            .sheet(isPresented: $showingAccountUsage) {
                NavigationStack {
                    UsageView(report: store.cachedUsage, machineName: "All machines", status: store.cacheStatus ?? "Saved readings retain their original age.")
                        .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { showingAccountUsage = false } } }
                }
                #if os(macOS)
                .frame(minWidth: 480, idealWidth: 600, minHeight: 500, idealHeight: 700)
                #endif
            }
        }
        #if os(macOS)
        .frame(minWidth: 500, idealWidth: 620, minHeight: 500, idealHeight: 700)
        #endif
    }

    private func usageRefresh(for host: HostStore) -> (@MainActor () async -> Void)? {
        guard host.can("usage") else { return nil }
        return {
            let request = Task<Void, Never> { @MainActor in await host.refreshUsage() }
            await request.value
        }
    }

    private func prepareForget(_ machine: RelayMachine, displayedName: String) {
        guard !machine.online, forgetTask == nil else { return }
        forgetRequest = ForgetRequest(machine: machine, displayedName: displayedName,
                                      relayAddress: store.relayAddress, accountID: store.account?.accountId)
    }

    private func forget(_ request: ForgetRequest) {
        guard forgetTask == nil else { return }
        forgetError = nil
        forgetTask = Task { @MainActor in
            defer { forgetTask = nil }
            guard store.relayAddress == request.relayAddress, store.account?.accountId == request.accountID,
                  store.roster.contains(where: { $0.id == request.machine.id && $0.name == request.machine.name && !$0.online })
            else {
                forgetError = "The connection or machine changed. Review the list before forgetting it."
                return
            }
            do { try await store.forget(request.machine.id) }
            catch { forgetError = "Couldn’t forget \(request.displayedName): \(error.localizedDescription)" }
        }
    }
}

private struct MachineView: View {
    let host: HostStore
    let updateHost: HostStore?
    let onUsage: () -> Void
    let onSessions: () -> Void
    @State private var showingDiagnostics = false
    private var updateSource: HostStore {
        updateHost ?? host
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Label(host.name, systemImage: host.isLocal ? "laptopcomputer" : "desktopcomputer").font(.headline)
                Spacer()
                Text(host.isLive ? (host.isLocal ? "This Mac" : "Online") : host.isConnecting ? "Connecting" : "Offline")
                    .font(.callout).foregroundStyle(.secondary)
            }
            if host.isLive {
                Text("\(host.sessions.count) \(host.sessions.count == 1 ? "session" : "sessions")")
                    .font(.callout).foregroundStyle(.secondary)
            } else {
                Text(host.status).font(.callout).foregroundStyle(.secondary)
            }
            if let reason = host.readOnlyReason {
                Label("View only", systemImage: "eye")
                Text(reason).font(.caption).foregroundStyle(.secondary)
            }
            HStack {
                Button("Show Sessions", action: onSessions).disabled(!host.isLive)
                if host.can("usage") || host.usage != nil {
                    Button("Usage", action: onUsage)
                }
                if host.isLocal {
                    Button("Host Details") { showingDiagnostics = true }.disabled(!host.isLive)
                }
            }
            .buttonStyle(.bordered)
            if updateSource.capabilities.contains("update") || updateSource.update != nil {
                Divider()
                MachineUpdateView(host: updateSource)
            }
        }
        .padding(.vertical, 4)
        .sheet(isPresented: $showingDiagnostics) { LocalHostDetailsView(host: host) }
    }
}

private struct MachineUpdateView: View {
    let host: HostStore
    @State private var note: String?
    @State private var pendingInstall: Install?
    @State private var requestTask: Task<Void, Never>?

    private struct Install: Identifiable {
        let id = UUID()
        let connection: UUID
        let instance: String
        let version: String
    }

    private var state: JSONValue? {
        host.update
    }

    private var attempt: MutationAttempt? {
        host.attempts["update"]
    }

    private var phase: String {
        let value = state?["phase"]?.string ?? "idle"
        return ["checking", "downloading", "waiting", "installing"].contains(value) ? value : "idle"
    }

    private var unconfirmed: Bool {
        guard let attempt, !attempt.isSending else { return false }
        return !["accepted", "refused"].contains(attempt.result?["outcome"]?.string ?? "")
    }

    private var canCheck: Bool {
        host.canCheckForUpdates && requestTask == nil
    }

    private var available: String? {
        state?["available"]?.string
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Host updates").font(.headline)
            if let current = state?["current"]?.string {
                Text("Installed \(current)").font(.callout)
            }
            Text(statusText).font(.callout).foregroundStyle(.secondary)
            if phase == "downloading", let value = state?["progress"]?.number, value.isFinite {
                ProgressView(value: min(100, max(0, value)), total: 100).accessibilityLabel("Download progress")
            }
            if let checked = SessionPresentation.date(state?["checkedAt"]?.string) {
                Text("Last checked \(SessionPresentation.timestamp(checked))").font(.caption).foregroundStyle(.secondary)
            }
            if let last = state?["last"], last != .null {
                Text(lastText(last)).font(.caption).foregroundStyle(.secondary)
            }
            if let notes = state?["notes"]?.string, !notes.isEmpty {
                DisclosureGroup("Release notes") { Text(notes).textSelection(.enabled) }
            }
            if let note {
                Text(note).font(.callout).foregroundStyle(.secondary)
            }
            if let attempt {
                if attempt.isSending {
                    ProgressView("Sending request…")
                } else if unconfirmed {
                    Text(attempt.error ?? "Delivery was not confirmed. Check the machine’s update state before trying again.")
                        .font(.callout).foregroundStyle(.orange)
                    Button("Retry Same Request") {
                        requestTask = Task { @MainActor in
                            await host.retry("update")
                            requestTask = nil
                        }
                    }
                    .disabled(!host.can("update") || !attempt.canRetry || attempt.hostInstance != host.instance || requestTask != nil)
                    if !attempt.canRetry {
                        Text("This request can no longer be retried here. Check the update at the machine.").font(.caption).foregroundStyle(.secondary)
                    }
                } else if let result = attempt.result {
                    Text(result["outcome"]?.string == "accepted"
                        ? "Request accepted. The machine will report its progress here."
                        : result["reason"]?.string ?? "The machine refused the request.")
                        .font(.callout).foregroundStyle(.secondary)
                }
            }
            HStack {
                Button("Check for Updates") { ask("update.check") }.disabled(!canCheck)
                if let available {
                    Button("Install \(available)") {
                        pendingInstall = Install(connection: host.connectionID, instance: host.instance, version: available)
                    }
                    .disabled(!canCheck)
                }
            }
            .buttonStyle(.bordered)
        }
        .confirmationDialog("Install update on \(host.name)?", isPresented: Binding(get: { pendingInstall != nil }, set: {
            if !$0 {
                pendingInstall = nil
            }
        }), presenting: pendingInstall) { install in
            Button("Install \(install.version)", role: .destructive) { ask("update.install", install: install) }
            Button("Cancel", role: .cancel) { pendingInstall = nil }
        } message: { install in
            Text("Ask \(host.name) to install \(install.version) when sessions are idle. Its app and host will restart. The host may proceed after six hours even if a session is still busy.")
        }
    }

    private var statusText: String {
        guard host.isLive else { return "Offline · last received update state" }
        guard state != nil else { return "Waiting for update status…" }
        guard host.capabilities.contains("update") else { return "Updates are unavailable on this connection." }
        guard state?["installer"]?.bool == true else { return state?["unavailable"]?.string ?? "The installed host app must be running to update." }
        switch phase {
        case "checking": return "Checking for updates…"
        case "downloading": return "Downloading \(available ?? "the update")…"
        case "waiting": return state?["waiting"]?.string ?? "Waiting for sessions to become idle."
        case "installing": return "Installing · the host and app will restart."
        default:
            if let available {
                return "\(available) is available."
            }
            return state?["checkedAt"]?.string == nil ? "Updates have not been checked yet." : "No newer release found at the last check."
        }
    }

    private func lastText(_ last: JSONValue) -> String {
        switch last["outcome"]?.string {
        case "updated": "Updated to \(last["to"]?.string ?? "the new release")."
        case "current": "Already up to date when last asked."
        case "refused": "Not updated: \(last["reason"]?.string ?? "the request was refused")"
        default: "Update failed: \(last["reason"]?.string ?? "no reason was given")"
        }
    }

    private func ask(_ operation: String, install: Install? = nil) {
        guard canCheck else { return }
        if let install, install.connection != host.connectionID || install.instance != host.instance || install.version != available {
            note = "The machine or available release changed. Review its current state before installing."
            return
        }
        let connection = host.connectionID
        let instance = host.instance
        let request = UUID()
        let operation = BridgeOperation(op: operation, data: ["request": .string(request.uuidString.lowercased())], requestID: request)
        note = nil
        // The store retains the UUID and result when this row is dismissed.
        requestTask = Task { @MainActor in
            defer { requestTask = nil }
            guard connection == host.connectionID, instance == host.instance, host.can("update") else {
                note = "The connection changed. Review the machine’s current state before trying again."
                return
            }
            await host.perform(operation, slot: "update")
        }
    }
}

private extension HostStore {
    var canCheckForUpdates: Bool {
        can("update") && update?["installer"]?.bool == true
            && !["checking", "downloading", "waiting", "installing"].contains(update?["phase"]?.string ?? "idle")
            && attempts["update"]?.isSending != true && attempts["update"]?.uncertain != true
    }
}
