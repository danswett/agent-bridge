import SwiftUI
import AgentBridgeAppleCore

@main
struct AgentBridgeApp: App {
    @State private var store = BridgeStore()
    @State private var selectedSessionID: String?

    var body: some Scene {
        WindowGroup {
            NavigationSplitView {
                List(store.sessions, selection: $selectedSessionID) { session in
                    NavigationLink(value: session.id) {
                        VStack(alignment: .leading, spacing: 4) {
                            Text(session.title).lineLimit(2)
                            Text("\(session.agent) · \(session.status)")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
                .navigationTitle("Agent Bridge")
                .toolbar {
                    Button("Refresh", systemImage: "arrow.clockwise") {
                        Task { await store.refresh() }
                    }
                }
            } detail: {
                if let id = selectedSessionID, let session = store.sessions.first(where: { $0.id == id }) {
                    Form {
                        LabeledContent("Agent", value: session.agent)
                        LabeledContent("Status", value: session.status)
                        LabeledContent("Session ID", value: session.id)
                    }
                    .navigationTitle(session.title)
                } else {
                    ContentUnavailableView("Select a session", systemImage: "terminal")
                }
            }
            .safeAreaInset(edge: .bottom) {
                Text(store.connectionStatus)
                    .font(.caption).foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(8)
            }
            .task { await store.refresh() }
        }
    }
}
