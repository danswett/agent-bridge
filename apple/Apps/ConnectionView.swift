import SwiftUI
#if os(macOS)
    import AppKit
#else
    import UIKit
#endif

struct ConnectionView: View {
    @Bindable var store: BridgeStore
    @Environment(\.dismiss) private var dismiss
    @State private var relayAddress: String
    @State private var deviceName: String
    @State private var profile: String
    @State private var copiedCode = false

    init(store: BridgeStore) {
        self.store = store
        _relayAddress = State(initialValue: store.relayAddress)
        _deviceName = State(initialValue: store.deviceName)
        _profile = State(initialValue: store.profile)
    }

    var body: some View {
        NavigationStack {
            Form {
                Section("Connection") {
                    Text(store.connectionStatus)
                    if !store.isSample {
                        Text(store.remoteStatus).font(.caption).foregroundStyle(.secondary)
                    }
                    Button("Reconnect", action: connect).disabled(store.isSigningIn)
                    if store.isLive || store.isConnecting {
                        Button("Disconnect", action: store.disconnect)
                    }
                }
                Section("GitHub account") {
                    if let account = store.account {
                        Label(account.login, systemImage: "person.crop.circle")
                        Button("Sign Out", role: .destructive, action: store.signOut)
                    } else if store.isSigningIn {
                        if let code = store.signInCode {
                            Text(code.userCode).font(.title2.monospaced()).textSelection(.enabled)
                            Button(copiedCode ? "Code Copied" : "Copy Code", systemImage: "doc.on.doc") {
                                copy(code.userCode)
                                copiedCode = true
                            }
                            Link("Continue on GitHub", destination: code.verificationUri)
                        } else {
                            ProgressView("Requesting sign-in code…")
                        }
                        Button("Cancel Sign In", role: .cancel, action: store.cancelSignIn)
                    } else {
                        Button("Sign In with GitHub") {
                            applySettings()
                            copiedCode = false
                            store.signIn()
                        }
                    }
                    if let status = store.signInStatus {
                        Text(status).font(.callout).foregroundStyle(.secondary)
                    }
                }
                Section {
                    TextField("Relay address", text: $relayAddress)
                        .autocorrectionDisabled()
                    #if os(iOS)
                        .textInputAutocapitalization(.never)
                        .keyboardType(.URL)
                    #endif
                    TextField("This device’s name", text: $deviceName)
                    #if os(macOS)
                        TextField("Local host profile", text: $profile).autocorrectionDisabled()
                    #endif
                    Button("Apply and Reconnect", action: connect)
                } header: {
                    Text("Settings")
                } footer: {
                    Text("Sign in with the same GitHub account as your hosts to connect your machines.")
                }
                .disabled(store.isSigningIn)
                Section("Preview") {
                    Button(store.isSample ? "Close Sample Sessions" : "Explore Sample Sessions") {
                        store.cancelSignIn()
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
            .navigationTitle("Connection")
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } } }
        }
        #if os(macOS)
        .frame(minWidth: 480, idealWidth: 540, minHeight: 560, idealHeight: 700)
        #endif
    }

    private func applySettings() {
        store.relayAddress = relayAddress.trimmingCharacters(in: .whitespacesAndNewlines)
        store.deviceName = deviceName.trimmingCharacters(in: .whitespacesAndNewlines)
        store.profile = profile.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private func connect() {
        applySettings()
        store.connect()
    }

    private func copy(_ text: String) {
        #if os(macOS)
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
        #else
            UIPasteboard.general.string = text
        #endif
    }
}
