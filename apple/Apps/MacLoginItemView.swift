#if os(macOS)
    import ServiceManagement
    import SwiftUI

    struct MacLoginItemView: View {
        @Environment(\.scenePhase) private var scenePhase
        @State private var status = SMAppService.mainApp.status
        @State private var error: String?

        var body: some View {
            Section("Startup") {
                Toggle("Open Agent Bridge at login", isOn: Binding(get: {
                    status == .enabled || status == .requiresApproval
                }, set: { setEnabled($0) }))
                if status == .requiresApproval {
                    Text("Allow Agent Bridge in Login Items to finish enabling it.").font(.caption)
                    Button("Open Login Items Settings") { SMAppService.openSystemSettingsLoginItems() }
                } else if status == .notFound {
                    Text("Login item registration is unavailable for this app build.").font(.caption)
                }
                if let error {
                    Text(error).font(.caption).foregroundStyle(.secondary)
                }
            }
            .onAppear { status = SMAppService.mainApp.status }
            .onChange(of: scenePhase) {
                if scenePhase == .active {
                    status = SMAppService.mainApp.status
                }
            }
        }

        private func setEnabled(_ enabled: Bool) {
            error = nil
            do {
                if enabled {
                    try SMAppService.mainApp.register()
                } else {
                    try SMAppService.mainApp.unregister()
                }
            } catch { self.error = "Could not change the login item: \(error.localizedDescription)" }
            status = SMAppService.mainApp.status
        }
    }
#endif
