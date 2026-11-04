import SwiftUI

struct SettingsNotificationsView: View {
    @Bindable var notifications: BridgeNotificationManager

    var body: some View {
        Form {
            Section {
                Toggle("Enable notifications", isOn: Binding(get: { notifications.enabled }, set: { value in
                    Task { await notifications.setEnabled(value) }
                }))
                .disabled(notifications.isRequestingPermission)
                Toggle("Quiet mode", isOn: $notifications.quiet)
                    .disabled(!notifications.enabled)
                Toggle("Notify when a turn finishes", isOn: $notifications.notifyFinished)
                    .disabled(!notifications.enabled)
                Toggle("Show session and question previews", isOn: $notifications.showPreviews)
                    .disabled(!notifications.enabled)
                Text(notifications.status)
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            } header: {
                Text("Notifications")
            } footer: {
                Text("Previews can include workspace names and the agent’s question. With previews off, notifications show only that a session needs attention or finished its turn.")
            }
            #if os(iOS)
                Section {
                    Text("Notifications are available while Agent Bridge is open. Background push notifications are not configured.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }
            #else
                Section {
                    Text("The Dock badge counts sessions waiting for an answer while the app is running.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }
            #endif
        }
        .formStyle(.grouped)
        .navigationTitle("Notifications")
        .task { await notifications.refreshAuthorization() }
    }
}
