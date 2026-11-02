import AgentBridgeAppleCore
import SwiftUI

struct MachinesView: View {
    @Bindable var store: BridgeStore
    @Environment(\.dismiss) private var dismiss
    @State private var usageHost: HostStore?

    private var displayedHosts: [HostStore] {
        guard !store.isSample else { return [] }
        let localIDs = Set(store.hosts.filter(\.isLocal).compactMap { $0.hello?["machine"]?["id"]?.string })
        return store.hosts.filter { $0.isLocal || !localIDs.contains($0.hello?["machine"]?["id"]?.string ?? "") }
            .sorted { $0.isLocal && !$1.isLocal }
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

    var body: some View {
        NavigationStack {
            List {
                Section {
                    Text(store.connectionStatus).foregroundStyle(.secondary)
                    if store.isSample {
                        Text("Sample sessions have no connected machine.")
                    } else if store.hosts.isEmpty, knownWithoutHost.isEmpty {
                        ContentUnavailableView("No machines connected", systemImage: "desktopcomputer",
                                               description: Text("Connect a local host or sign in from Connection settings."))
                    }
                }
                ForEach(displayedHosts) { host in
                    Section {
                        MachineView(host: host, updateHost: remoteConnection(for: host), onUsage: { usageHost = host }) {
                            store.selectedMachine = host.id
                            dismiss()
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
                    }
                }
            }
            .navigationTitle("Machines")
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } } }
            .sheet(item: $usageHost) { host in
                NavigationStack {
                    UsageView(report: host.usage, machineName: host.name, status: host.status,
                              onRefresh: host.can("usage") ? { @MainActor @Sendable in await Task { await host.refreshUsage() }.value } : nil)
                        .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { usageHost = nil } } }
                }
                #if os(macOS)
                .frame(minWidth: 440, idealWidth: 560, minHeight: 480, idealHeight: 640)
                #endif
            }
        }
        #if os(macOS)
        .frame(minWidth: 500, idealWidth: 620, minHeight: 500, idealHeight: 700)
        #endif
    }
}

private struct MachineView: View {
    let host: HostStore
    let updateHost: HostStore?
    let onUsage: () -> Void
    let onSessions: () -> Void
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
            }
            .buttonStyle(.bordered)
            if updateSource.capabilities.contains("update") || updateSource.update != nil {
                Divider()
                MachineUpdateView(host: updateSource)
            }
        }
        .padding(.vertical, 4)
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
        host.can("update") && state?["installer"]?.bool == true && phase == "idle"
            && attempt?.isSending != true && !unconfirmed && requestTask == nil
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
                    .disabled(!host.can("update") || attempt.hostInstance != host.instance || requestTask != nil)
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
