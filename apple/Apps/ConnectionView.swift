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
    @AppStorage("bridge.reading.showDetails") private var showDetails = true

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
                    if let cacheStatus = store.cacheStatus {
                        Text(cacheStatus).font(.caption).foregroundStyle(.secondary)
                    }
                    Button("Reconnect", action: connect).disabled(store.isSigningIn || store.isFixture)
                    if store.isLive || store.isConnecting {
                        Button("Disconnect", action: store.disconnect).disabled(store.isFixture)
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
                }.disabled(store.isFixture)
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
                .disabled(store.isSigningIn || store.isFixture)
                Section {
                    NavigationLink { SettingsNotificationsView(notifications: store.notifications) } label: { Label("Notifications", systemImage: "bell") }
                }
                Section("Reading") {
                    Toggle("Show reasoning and tool history", isOn: $showDetails)
                }
                #if os(macOS)
                    MacLoginItemView().disabled(store.isFixture)
                #endif
                Section("About Agent Bridge") {
                    if let version = bundleValue("CFBundleShortVersionString") {
                        LabeledContent("Version", value: version)
                    }
                    if let build = bundleValue("CFBundleVersion") {
                        LabeledContent("Build", value: build)
                    }
                    #if DEBUG
                        Text("Development build").font(.caption).foregroundStyle(.secondary)
                    #else
                        if bundleValue("CFBundleShortVersionString") == nil, bundleValue("CFBundleVersion") == nil {
                            Text("Version information is unavailable.").foregroundStyle(.secondary)
                        }
                    #endif
                }
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
                }.disabled(store.isFixture)
            }
            .formStyle(.grouped)
            .navigationTitle("Connection")
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } } }
        }
        #if os(macOS)
        .frame(minWidth: 480, idealWidth: 540, minHeight: 560, idealHeight: 700)
        #endif
    }

    private func bundleValue(_ key: String) -> String? {
        guard let value = Bundle.main.object(forInfoDictionaryKey: key) as? String, !value.isEmpty else { return nil }
        return value
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
