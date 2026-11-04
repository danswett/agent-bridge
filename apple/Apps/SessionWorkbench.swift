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
    #if os(macOS)
        @Environment(\.openSettings) private var openSettings
    #endif

    private var selected: SessionEntry? {
        (store.entries + store.endedEntries).first { $0.id == store.selectedSessionID }
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
        #if os(macOS)
            navigation
                .safeAreaInset(edge: .top, spacing: 0) {
                    if store.notifications.actionStatus != nil || store.canonicalHosts.contains(where: { $0.discoveryHold != nil }) {
                        VStack(alignment: .leading, spacing: 4) {
                            actionReceipt
                            discoveryWarnings
                        }
                        .font(.caption).foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(12).background(.bar)
                    }
                }
        #else
            VStack(spacing: 0) {
                navigation
                connectionFooter
            }
        #endif
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
                            machinesButton
                            #if os(macOS)
                                SettingsLink { Label("Settings…", systemImage: "gearshape") }
                            #else
                                Button("Connection Settings", systemImage: "network", action: showConnection)
                            #endif
                            Button("Reconnect", systemImage: "arrow.clockwise", action: store.connect).disabled(store.isSample || store.isFixture)
                            Button("Launch or Resume", systemImage: "plus") { showingLaunch = true }
                                .disabled(!store.hosts.contains { $0.can("sessions.launch") || $0.capabilities.contains("launch.trust") || $0.attempts["launch"] != nil })
                        } label: { Image(systemName: "line.3.horizontal.decrease") }
                            .accessibilityLabel("Session filters and connections")
                    }
                    ToolbarItem {
                        machinesButton
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
        #if os(iOS)
            .sheet(isPresented: $showingConnection) { ConnectionView(store: store) }
        #endif
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
                      awaitingResponse: !entry.ended && entry.host?.isAwaitingResponse(for: entry.session.id) == true,
                      historyConnectionID: entry.host.flatMap { $0.isLive ? $0.connectionID : nil },
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
                            .frame(maxHeight: 280, alignment: .bottom)
                            // The cap limits growth; it must not reserve empty space around a short draft.
                            .fixedSize(horizontal: false, vertical: true)
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
            Label(store.connectionPresentation.emptyTitle, systemImage: "terminal")
        } description: {
            Text(store.connectionPresentation.emptyDetail)
        } actions: {
            ViewThatFits(in: .horizontal) {
                HStack {
                    connectionActions
                }
                .fixedSize(horizontal: true, vertical: false)
                VStack(spacing: 8) {
                    connectionActions
                }
            }
        }
    }

    @ViewBuilder
    private var connectionActions: some View {
        if !store.isSample, !store.isFixture, store.account == nil {
            Button(store.isSigningIn ? "Continue Sign-In" : "Sign In with GitHub") {
                showConnection()
                if !store.isSigningIn {
                    store.signIn()
                }
            }
        }
        #if os(macOS)
            SettingsLink { Label("Settings…", systemImage: "gearshape") }
        #else
            Button("Connection Settings", action: showConnection)
        #endif
        if !store.isSample {
            Button("Explore Sample Sessions", action: store.showSamples)
        }
    }

    private func showConnection() {
        #if os(macOS)
            openSettings()
        #else
            showingConnection = true
        #endif
    }

    private var machinesButton: some View {
        Button("Machines", systemImage: "desktopcomputer") { showingMachines = true }
            .help(machineStatusHelp)
            .accessibilityHint(machineStatusHelp)
    }

    private var machineStatusHelp: String {
        var lines = [store.connectionStatus]
        if !store.isSample, !store.isFixture, store.connectionPresentation.remote != store.connectionStatus {
            lines.append(store.connectionPresentation.remote)
        }
        return lines.joined(separator: "\n")
    }

    @ViewBuilder private var actionReceipt: some View {
        if let actionStatus = store.notifications.actionStatus {
            HStack(alignment: .top) {
                Label(actionStatus, systemImage: "bubble.left")
                Spacer(minLength: 8)
                Button("Dismiss action result", systemImage: "xmark", action: store.notifications.dismissActionStatus)
                    .labelStyle(.iconOnly)
            }
        }
    }

    private var discoveryWarnings: some View {
        ForEach(store.canonicalHosts.filter { $0.discoveryHold != nil }) { host in
            Label("\(host.name): \(host.discoveryHold ?? "")", systemImage: "exclamationmark.triangle")
        }
    }

    private var connectionFooter: some View {
        VStack(alignment: .leading, spacing: 4) {
            actionReceipt
            Label(store.connectionStatus, systemImage: store.isLive ? "checkmark.circle" : "network")
            if !store.isSample, !store.isFixture, store.connectionPresentation.remote != store.connectionStatus {
                Label(store.connectionPresentation.remote, systemImage: "network")
            }
            discoveryWarnings
        }
        .font(.caption).foregroundStyle(.secondary)
        .frame(maxWidth: .infinity, alignment: .leading).padding(12).background(.bar)
        .accessibilityElement(children: .combine)
    }
}
