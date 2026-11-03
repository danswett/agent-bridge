import SwiftUI

@main
struct AgentBridgeApp: App {
    @State private var store = BridgeStore()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup(id: "workbench") {
            SessionWorkbench(store: store)
            #if os(macOS)
                .frame(minWidth: 720, minHeight: 480)
            #endif
        }
        #if os(macOS)
        .defaultSize(width: 1080, height: 760)
        #endif
        .onChange(of: scenePhase) { _, phase in
            store.notifications.setForeground(phase == .active)
            #if os(iOS)
                if phase == .background {
                    store.suspend()
                }
                if phase == .active, store.hasStarted {
                    store.resume()
                }
            #endif
        }
        #if os(macOS)
            MenuBarExtra {
                BridgeMenuView(store: store)
            } label: {
                let waiting = store.entries.lazy.filter { $0.session.question != nil }.count
                Label(waiting == 0 ? "Agent Bridge" : "\(waiting)", systemImage: "terminal")
            }
        #endif
    }
}

#if os(macOS)
    private struct BridgeMenuView: View {
        @Bindable var store: BridgeStore
        @Environment(\.openWindow) private var openWindow
        var body: some View {
            Text(store.connectionStatus)
            Divider()
            ForEach(Array(store.entries.filter { $0.session.question != nil }.prefix(8))) { entry in
                Button("\(entry.machineName): \(entry.session.title)") {
                    store.selectedSessionID = entry.id
                    openWindow(id: "workbench")
                    NSApp.activate(ignoringOtherApps: true)
                }
            }
            Button("Open Agent Bridge") { openWindow(id: "workbench"); NSApp.activate(ignoringOtherApps: true) }
            Button("Reconnect", action: store.connect).disabled(store.isSample || store.isFixture)
            Divider()
            Button("Quit Agent Bridge") { NSApp.terminate(nil) }
        }
    }
#endif
