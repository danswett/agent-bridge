import AgentBridgeAppleCore
import SwiftUI

struct SessionWorkbench: View {
    @Bindable var store: BridgeStore
    @State private var search = ""
    @State private var showingConnection = false
    @State private var showingMachines = false
    @State private var showingMobileInspector = false
    @State private var showingLaunch = false
    @State private var needsOnly = false
    @FocusState private var searchFocused: Bool
    @Environment(\.openWindow) private var openWindow
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private var selected: SessionEntry? {
        store.sessionEntry(id: store.selectedSessionID)
    }

    private var needsYou: [SessionEntry] {
        store.entries.filter { $0.session.question != nil }.sorted {
            ($0.session.question?["askedAt"]?.string ?? $0.session.updatedAt ?? "") < ($1.session.question?["askedAt"]?.string ?? $1.session.updatedAt ?? "")
        }
    }

    private var filtered: [SessionEntry] {
        let terms = search.split(whereSeparator: \.isWhitespace).map(String.init)
        let matched = store.entries.filter { entry in
            if !terms.isEmpty {
                let fields = [entry.session.title, entry.session.agent, entry.session.repository ?? "", entry.session.branch ?? "", entry.session.cwd ?? "", entry.machineName]
                return terms.allSatisfy { term in fields.contains { $0.localizedCaseInsensitiveContains(term) } }
            }
            return store.includesSelectedMachine(entry.host) && (!needsOnly || entry.session.question != nil)
        }
        if !terms.isEmpty {
            let query = search.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            return matched.sorted {
                let leftPrefix = $0.session.title.lowercased().hasPrefix(query)
                let rightPrefix = $1.session.title.lowercased().hasPrefix(query)
                if leftPrefix != rightPrefix {
                    return leftPrefix
                }
                if $0.session.updatedAt != $1.session.updatedAt {
                    return ($0.session.updatedAt ?? "") > ($1.session.updatedAt ?? "")
                }
                return $0.id < $1.id
            }
        }
        return store.sorted(matched)
    }

    var body: some View {
        VStack(spacing: 0) {
            navigation
            connectionFooter
        }
    }

    private var navigation: some View {
        NavigationSplitView {
            List(selection: $store.selectedSessionID) {
                if search.isEmpty, !needsOnly, !needsYou.isEmpty {
                    Section("Needs you · \(needsYou.count)") { ForEach(needsYou) { entry in row(entry) } }
                }
                Section(needsOnly ? "Needs you" : search.isEmpty ? "Sessions" : "Search results") {
                    ForEach(filtered) { entry in row(entry) }
                }
                if search.isEmpty, !needsOnly, !store.endedEntries.isEmpty {
                    Section("Recently ended") {
                        ForEach(store.endedEntries.filter { store.includesSelectedMachine($0.host) }) { entry in row(entry).opacity(0.6) }
                    }
                }
            }
            .overlay {
                if store.entries.isEmpty, store.endedEntries.isEmpty {
                    emptyState
                } else if filtered.isEmpty, !search.isEmpty {
                    ContentUnavailableView.search(text: search)
                }
            }
            .navigationTitle("Sessions")
            .navigationSplitViewColumnWidth(min: 260, ideal: 330, max: 440)
            #if os(macOS)
                .frame(minWidth: 260)
            #endif
                .searchable(text: $search, prompt: "Search all machines")
                .searchFocused($searchFocused)
                .toolbar {
                    ToolbarItem {
                        Menu {
                            Button("Search Sessions", systemImage: "magnifyingglass") { searchFocused = true }
                                .keyboardShortcut("k", modifiers: .command)
                            Toggle("Needs you", isOn: $needsOnly)
                            Picker("Order", selection: $store.sortOrder) {
                                Text("Your order").tag("manual"); Text("Most recent").tag("recent"); Text("Machine").tag("machine")
                            }
                            Picker("Machine", selection: $store.selectedMachine) {
                                Text("All machines").tag(String?.none)
                                ForEach(store.canonicalHosts) { host in Text(host.name).tag(Optional(host.id)) }
                            }
                            Button("Machines", systemImage: "desktopcomputer") { showingMachines = true }
                            Button("Connection Settings", systemImage: "network") { showingConnection = true }
                            Button("Reconnect", systemImage: "arrow.clockwise", action: store.connect).disabled(store.isSample || store.isFixture)
                            Button("Launch or Resume", systemImage: "plus") { showingLaunch = true }
                                .disabled(!store.canOpenLauncher)
                        } label: { Image(systemName: "line.3.horizontal.decrease") }
                            .accessibilityLabel("Session filters and connections")
                    }
                    ToolbarItem {
                        Button("Machines", systemImage: "desktopcomputer") { showingMachines = true }
                    }
                }
        } detail: {
            if let entry = selected {
                detail(entry)
            } else {
                ContentUnavailableView("Select a session", systemImage: "terminal", description: Text("Activity and questions appear here."))
            }
        }
        .tint(.teal)
        .sheet(isPresented: $showingConnection) { ConnectionView(store: store) }
        .sheet(isPresented: $showingMachines) { MachinesView(store: store) }
        .sheet(isPresented: $showingLaunch) { LaunchView(store: store) }
        .task {
            store.onOpenSession = {
                #if os(macOS)
                    openWindow(id: "workbench")
                    NSApp.activate(ignoringOtherApps: true)
                #endif
            }
            store.notifications.setForeground(true)
            store.startOnce()
        }
        .onChange(of: store.selectedSessionID) {
            showingMobileInspector = false
            store.notifications.focus(machineID: selected?.host?.hello?["machine"]?["id"]?.string, sessionID: selected?.session.id)
            if let selected {
                store.markRead(selected)
            }
        }
        .onChange(of: selected?.session.updatedAt) {
            if let selected {
                store.markRead(selected)
            }
        }
        .onChange(of: store.account?.accountId) { store.selectedSessionID = nil }
    }

    private func row(_ entry: SessionEntry) -> some View {
        NavigationLink(value: entry.id) {
            VStack(alignment: .leading, spacing: 4) {
                SessionRow(session: entry.session)
                HStack(spacing: 6) {
                    if store.pinned.contains(entry.id) {
                        Image(systemName: "pin.fill")
                    }
                    if store.isUnread(entry) {
                        Circle().frame(width: 6, height: 6).accessibilityLabel("Unread")
                    }
                    Text(entry.machineName)
                    if entry.ended {
                        Text("Ended")
                    } else if entry.host?.isLive == false {
                        Text("Offline")
                    }
                }.font(.caption2).foregroundStyle(.secondary)
            }
        }
        .contextMenu {
            Button(store.pinned.contains(entry.id) ? "Unpin" : "Pin", systemImage: "pin") { store.togglePin(entry.id) }
            Button("Move Up", systemImage: "arrow.up") { store.move(entry.id, by: -1) }
            Button("Move Down", systemImage: "arrow.down") { store.move(entry.id, by: 1) }
            Button("Mark Read") { store.markRead(entry) }
        }
    }

    private func reader(_ entry: SessionEntry, layout: SessionDetailLayout) -> some View {
        SessionDetail(session: entry.session, readOnlyReason: entry.host?.readOnlyReason,
                      machineName: entry.machineName, canAnswer: canAnswer(entry),
                      onLoadHistory: entry.host.map { host in
                          { @MainActor @Sendable id in
                              // Leaving the detail must not cancel the shared transport.
                              try await Task { try await host.call("session.get", data: ["id": .string(id)]) }.value
                          }
                      }, onLoadDelegate: delegateLoader(for: entry), onAskMainToStop: delegateStopDraft(for: entry), layout: layout) {
            if let host = entry.host, !entry.ended {
                SessionActions(sessionID: entry.session.id, store: store, host: host, presentation: .inspector)
                    .id(entry.id)
            }
        }
        .id(entry.id)
    }

    private func detail(_ entry: SessionEntry) -> some View {
        #if os(macOS)
            HSplitView {
                VStack(spacing: 0) {
                    reader(entry, layout: .conversation)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                    if let host = entry.host, !entry.ended {
                        Divider()
                        SessionActions(sessionID: entry.session.id, store: store, host: host, presentation: .composer)
                            .id(entry.id)
                            .frame(maxHeight: 280)
                    }
                }
                .frame(minWidth: 360, idealWidth: 660, maxWidth: .infinity)
                VStack(alignment: .leading, spacing: 0) {
                    Label("Session inspector", systemImage: "sidebar.right")
                        .font(.headline).padding(.horizontal, 16).padding(.vertical, 12)
                        .accessibilityAddTraits(.isHeader)
                    Divider()
                    reader(entry, layout: .inspector)
                }
                .frame(minWidth: 270, idealWidth: 320, maxWidth: 460, maxHeight: .infinity, alignment: .topLeading)
            }
        #else
            MobileSessionWorkspace(entry: entry, store: store, showingInspector: $showingMobileInspector) {
                reader(entry, layout: .conversation)
            } inspector: {
                reader(entry, layout: .inspector)
            }
            .id(entry.id)
        #endif
    }

    private func canAnswer(_ entry: SessionEntry) -> Bool {
        guard !entry.ended, let host = entry.host, host.can("sessions.answer"), host.readOnlyReason == nil,
              let raw = entry.session.question, let question = BridgeQuestion(json: raw) else { return false }
        return question.readOnlyReason(capabilities: host.capabilities, sessionAgent: entry.session.agent) == nil
    }

    private func delegateLoader(for entry: SessionEntry) -> (@MainActor (String, String) async throws -> JSONValue)? {
        guard let host = entry.host, !entry.ended else { return nil }
        let connection = host.connectionID
        return { sessionID, delegateID in
            guard sessionID == entry.session.id, store.selectedSessionID == entry.id,
                  host.sessions.first(where: { $0.id == sessionID })?.delegates.contains(where: { $0.id == delegateID }) == true
            else {
                throw BridgeOperationError("This delegate is no longer selected or listed.")
            }
            // The selection owns the connection; navigating away must not cancel the shared channel.
            let result = try await Task {
                try await host.call("session.get", data: ["id": .string(sessionID), "agent": .string(delegateID)], onlyOn: connection)
            }.value
            guard store.selectedSessionID == entry.id else { throw BridgeOperationError("The selected session changed.") }
            return result
        }
    }

    private func delegateStopDraft(for entry: SessionEntry) -> (@MainActor (String) -> Void)? {
        guard let host = entry.host, !entry.ended, host.can("sessions.reply"), entry.session.question == nil else { return nil }
        let connection = host.connectionID
        return { id in
            guard store.selectedSessionID == entry.id, host.connectionID == connection, host.can("sessions.reply"),
                  let session = host.sessions.first(where: { $0.id == entry.session.id }), session.question == nil,
                  session.delegates.contains(where: { $0.id == id && $0.status != "ended" }),
                  let draft = BridgeDelegatePresentation.stopDraft(delegateID: id, existing: host.replyDrafts[session.id] ?? "") else { return }
            host.replyDrafts[session.id] = draft
            #if os(iOS)
                showingMobileInspector = false
            #endif
        }
    }

    private var emptyState: some View {
        ContentUnavailableView {
            VStack(spacing: 12) {
                if store.isConnecting, !reduceMotion {
                    ProgressView().controlSize(.large)
                } else {
                    Image(systemName: store.isConnecting ? "network" : store.isLive ? "tray" : "person.crop.circle.badge.questionmark")
                        .font(.largeTitle)
                        .accessibilityHidden(true)
                }
                Text(store.isConnecting ? "Connecting" : store.isLive ? "No active sessions" : "Connect to Agent Bridge")
            }
        } description: {
            if store.account == nil, !store.isSample, let status = store.signInStatus {
                Text(status)
            } else if store.isLive, !store.canOpenLauncher, let reason = store.canonicalHosts.compactMap(\.readOnlyReason).first {
                Text("This machine is read-only: \(reason)")
            } else {
                Text(store.isLive ? "Sessions appear when an agent starts on a connected machine." : "Sign in with the GitHub account you use on your host.")
            }
        } actions: {
            if store.isLive, store.canOpenLauncher {
                Button("Launch or Resume", systemImage: "plus") { showingLaunch = true }
                    .buttonStyle(.borderedProminent)
            }
            if !store.isSample, !store.isFixture, store.account == nil {
                Button(store.isSigningIn ? "Continue Sign-In" : "Sign In with GitHub") {
                    showingConnection = true
                    if !store.isSigningIn {
                        store.signIn()
                    }
                }
            }
            Button("Connection Settings") { showingConnection = true }
            if !store.isSample {
                Button("Explore Sample Sessions", action: store.showSamples)
            }
        }
    }

    private var connectionFooter: some View {
        VStack(alignment: .leading, spacing: 4) {
            if let actionStatus = store.notifications.actionStatus {
                HStack(alignment: .top) {
                    Label(actionStatus, systemImage: "bubble.left")
                    Spacer(minLength: 8)
                    Button("Dismiss action result", systemImage: "xmark", action: store.notifications.dismissActionStatus)
                        .labelStyle(.iconOnly)
                }
            }
            Label(store.connectionStatus, systemImage: store.isLive ? "checkmark.circle" : "network")
            ForEach(store.canonicalHosts.filter { $0.discoveryHold != nil }) { host in
                Label("\(host.name): \(host.discoveryHold ?? "")", systemImage: "exclamationmark.triangle")
            }
        }
        .font(.caption).foregroundStyle(.secondary)
        .frame(maxWidth: .infinity, alignment: .leading).padding(12).background(.bar)
        .accessibilityElement(children: .combine)
    }
}
