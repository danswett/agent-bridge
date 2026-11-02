import AgentBridgeAppleCore
import SwiftUI

struct LocalHostDetailsView: View {
    let host: HostStore
    @Environment(\.dismiss) private var dismiss
    @State private var details: JSONValue?
    @State private var error: String?
    @State private var refreshing = false
    @State private var generation = UUID()

    var body: some View {
        NavigationStack {
            Form {
                Section("Local host") {
                    Text(host.name).font(.headline)
                    Text(host.status).foregroundStyle(.secondary)
                    if let details {
                        if let version = details["version"]?.string {
                            LabeledContent("Version", value: version + (details["dev"]?.bool == true ? " (development)" : ""))
                        }
                        if let profile = details["profile"]?.string {
                            LabeledContent("Profile", value: profile)
                        }
                        if let pid = details["processId"]?.number, pid.isFinite {
                            LabeledContent("Process", value: pid.formatted(.number.precision(.fractionLength(0))))
                        }
                        if let started = SessionPresentation.date(details["startedAt"]?.string) {
                            LabeledContent("Started", value: SessionPresentation.timestamp(started))
                        }
                        if let state = details["relay"]?["state"]?.string {
                            LabeledContent("Relay", value: state == "signedOut" ? "Signed out" : state.capitalized)
                        }
                        if let reason = details["readOnly"]?.string {
                            Label("View only", systemImage: "eye")
                            Text(reason)
                        }
                        if details["restartRequired"]?.bool == true {
                            Text("The host has settings that take effect after its next restart.").font(.caption)
                        }
                    }
                    if refreshing {
                        ProgressView("Reading host status…")
                    }
                    if let error {
                        Text(error).foregroundStyle(.secondary)
                    }
                    Button("Refresh", systemImage: "arrow.clockwise") { Task { await refresh() } }
                        .disabled(!host.isLive || refreshing)
                }
            }
            .formStyle(.grouped)
            .textSelection(.enabled)
            .navigationTitle("Host Details")
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } } }
            .task(id: host.connectionID) { await refresh() }
        }
        #if os(macOS)
        .frame(minWidth: 440, idealWidth: 540, minHeight: 360, idealHeight: 480)
        #endif
    }

    private func refresh() async {
        let token = UUID()
        generation = token
        details = nil
        error = nil
        guard host.isLocal, host.isLive else { refreshing = false; return }
        refreshing = true
        let connection = host.connectionID
        do {
            // Closing a diagnostics sheet must not cancel the shared channel.
            let result = try await Task { try await host.call("host.status", onlyOn: connection) }.value
            guard token == generation, connection == host.connectionID else { return }
            details = result
        } catch {
            if token == generation, connection == host.connectionID {
                self.error = error.localizedDescription
            }
        }
        if token == generation {
            refreshing = false
        }
    }
}
