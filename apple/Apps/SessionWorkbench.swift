import AgentBridgeAppleCore
import SwiftUI

struct SessionWorkbench: View {
    @State private var store = BridgeStore()
    @State private var selectedSessionID: String?
    @State private var search = ""
    @State private var showingConnection = false
    @Environment(\.scenePhase) private var scenePhase

    private var filteredSessions: [BridgeSession] {
        guard !search.isEmpty else { return store.sessions }
        return store.sessions.filter {
            [$0.title, $0.agent, $0.repository ?? "", $0.cwd ?? ""].contains { $0.localizedCaseInsensitiveContains(search) }
        }
    }

    var body: some View {
        NavigationSplitView {
            List(selection: $selectedSessionID) {
                if !store.sessions.isEmpty {
                    Section(store.machineName) {
                        ForEach(filteredSessions) { session in
                            NavigationLink(value: session.id) { SessionRow(session: session) }
                        }
                    }
                }
            }
            .overlay {
                if store.sessions.isEmpty {
                    emptyState
                } else if filteredSessions.isEmpty {
                    ContentUnavailableView.search(text: search)
                }
            }
            .navigationTitle("Sessions")
            .navigationSplitViewColumnWidth(min: 260, ideal: 320, max: 440)
            #if os(macOS)
                .frame(minWidth: 260)
            #endif
                .searchable(text: $search, prompt: "Search sessions")
                .toolbar {
                    ToolbarItem {
                        Button("Connection", systemImage: "network") { showingConnection = true }
                    }
                    #if os(macOS)
                        ToolbarItem {
                            Button("Reconnect", systemImage: "arrow.clockwise", action: store.connect)
                                .disabled(store.isConnecting || store.isSample)
                        }
                    #endif
                }
        } detail: {
            if let id = selectedSessionID, let session = store.sessions.first(where: { $0.id == id }) {
                SessionDetail(session: session, readOnlyReason: store.readOnlyReason)
            } else {
                ContentUnavailableView("Select a session", systemImage: "terminal",
                                       description: Text("Activity and questions appear here."))
            }
        }
        .safeAreaInset(edge: .bottom) { connectionFooter }
        .tint(.teal)
        .sheet(isPresented: $showingConnection) { ConnectionView(store: store) }
        .task {
            if ProcessInfo.processInfo.arguments.contains("--sample-sessions") {
                store.showSamples()
                selectedSessionID = store.sessions.first?.id
            } else {
                #if os(macOS)
                    store.connect()
                #endif
            }
        }
        .onChange(of: store.sessions.map(\.id)) {
            if let selectedSessionID, !store.sessions.contains(where: { $0.id == selectedSessionID }) {
                self.selectedSessionID = nil
            }
        }
        .onChange(of: scenePhase) { _, phase in
            #if os(iOS)
                if phase == .background {
                    store.disconnect()
                }
            #endif
        }
        .onDisappear { store.disconnect() }
    }

    private var emptyState: some View {
        ContentUnavailableView {
            Label(store.isConnecting ? "Connecting" : store.isLive ? "No active sessions" : "Connect to Agent Bridge", systemImage: "terminal")
        } description: {
            Text(store.isLive ? "Sessions will appear when an agent starts on your Mac." : "View your agents’ activity in one place.")
        } actions: {
            if store.isConnecting {
                ProgressView()
            } else if !store.isLive {
                Button("Connection Settings") { showingConnection = true }
                Button("Explore Sample Sessions", action: store.showSamples)
            }
        }
    }

    private var connectionFooter: some View {
        VStack(alignment: .leading, spacing: 6) {
            Label(store.connectionStatus, systemImage: store.isLive ? "checkmark.circle" : "network")
            if let hold = store.discoveryHold {
                Label(hold, systemImage: "exclamationmark.triangle")
            }
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(12)
        .background(.bar)
        .accessibilityElement(children: .combine)
    }
}
