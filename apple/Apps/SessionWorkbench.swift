import AgentBridgeAppleCore
import SwiftUI

struct SessionWorkbench: View {
    @Bindable var store: BridgeStore
    @State private var search = ""
    @State private var showingConnection = false
    @State private var showingMachines = false
    @State private var showingActions = false
    @State private var showingLaunch = false
    @State private var needsOnly = false
    @FocusState private var searchFocused: Bool
    @Environment(\.openWindow) private var openWindow

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
                                .disabled(!store.hosts.contains { $0.can("sessions.launch") || $0.capabilities.contains("launch.trust") || $0.attempts["launch"] != nil })
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
        .safeAreaInset(edge: .bottom) { connectionFooter }
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

    private func detail(_ entry: SessionEntry) -> some View {
        SessionDetail(session: entry.session, readOnlyReason: entry.host?.readOnlyReason,
                      machineName: entry.machineName, canAnswer: !entry.ended && entry.host?.can("sessions.answer") == true,
                      onLoadHistory: entry.host.map { host in
                          { @MainActor @Sendable id in
                              // Leaving the detail must not cancel the shared transport.
                              try await Task { try await host.call("session.get", data: ["id": .string(id)]) }.value
                          }
                      })
                      .id(entry.id)
                      .toolbar {
                          if let host = entry.host, !entry.ended {
                              ToolbarItem {
                                  Button(entry.session.question == nil ? "Reply / Actions" : "Answer", systemImage: entry.session.question == nil ? "paperplane" : "questionmark.bubble") { showingActions = true }
                                      .disabled(!host.isLive)
                              }
                          }
                      }
                      .sheet(isPresented: $showingActions) {
                          if let host = entry.host {
                              SessionActions(sessionID: entry.session.id, store: store, host: host).id(entry.id)
                          }
                      }
    }

    private var emptyState: some View {
        ContentUnavailableView {
            Label(store.isConnecting ? "Connecting" : store.isLive ? "No active sessions" : "Connect to Agent Bridge", systemImage: "terminal")
        } description: {
            Text(store.isLive ? "Sessions appear when an agent starts on a connected machine." : "Follow your agents across your machines.")
        } actions: {
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
            ForEach(store.hosts.filter { $0.discoveryHold != nil }) { host in
                Label("\(host.name): \(host.discoveryHold ?? "")", systemImage: "exclamationmark.triangle")
            }
        }
        .font(.caption).foregroundStyle(.secondary)
        .frame(maxWidth: .infinity, alignment: .leading).padding(12).background(.bar)
        .accessibilityElement(children: .combine)
    }
}
