import SwiftUI

struct ConnectionView: View {
    @Bindable var store: BridgeStore
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Form {
                Section("Connection") {
                    Text(store.connectionStatus)
                    #if os(macOS)
                        TextField("Host profile", text: $store.profile)
                            .autocorrectionDisabled()
                            .onSubmit(connect)
                        Button("Connect to Local Host", action: connect)
                            .disabled(store.isConnecting)
                        if store.isLive || store.isConnecting {
                            Button("Disconnect", action: store.disconnect)
                        }
                    #else
                        LabeledContent("Remote pairing", value: "Not available in this build")
                    #endif
                }
                Section("Preview") {
                    Button(store.isSample ? "Close Sample Sessions" : "Explore Sample Sessions") {
                        if store.isSample {
                            store.closeSamples()
                        } else {
                            store.showSamples()
                        }
                        dismiss()
                    }
                }
            }
            .formStyle(.grouped)
            .navigationTitle("Agent Bridge")
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } } }
        }
        #if os(macOS)
        .frame(width: 460, height: 360)
        #endif
    }

    private func connect() {
        store.connect()
        dismiss()
    }
}
