import SwiftUI

@main
struct AgentBridgeApp: App {
    var body: some Scene {
        WindowGroup {
            SessionWorkbench()
            #if os(macOS)
                .frame(minWidth: 720, minHeight: 480)
            #endif
        }
        #if os(macOS)
        .defaultSize(width: 1080, height: 760)
        #endif
    }
}
