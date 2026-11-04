import SwiftUI
#if os(macOS)
    import AppKit
#else
    import UIKit
#endif

struct ConnectionView: View {
    @Bindable var store: BridgeStore
    @Environment(\.dismiss) private var dismiss
    @Environment(\.openURL) private var openURL
    @State private var relayAddress: String
    @State private var deviceName: String
    @State private var profile: String
    @State private var copiedCode = false
    @AppStorage("bridge.reading.showDetails") private var showDetails = true
    #if os(macOS)
        @State private var selectedTab = "connection"
    #endif

    init(store: BridgeStore) {
        self.store = store
        _relayAddress = State(initialValue: store.relayAddress)
        _deviceName = State(initialValue: store.deviceName)
        _profile = State(initialValue: store.profile)
    }

    var body: some View {
        #if os(macOS)
            TabView(selection: $selectedTab) {
                Tab("Connection", systemImage: "network", value: "connection") {
                    Form { connectionSections }
                        .formStyle(.grouped)
                }
                Tab("General", systemImage: "gearshape", value: "general") {
                    Form {
                        generalSections
                        previewSection
                    }
                    .formStyle(.grouped)
                }
                Tab("Notifications", systemImage: "bell", value: "notifications") {
                    SettingsNotificationsView(notifications: store.notifications)
                }
                Tab("About", systemImage: "info.circle", value: "about") {
                    Form { aboutSection }
                        .formStyle(.grouped)
                }
            }
            .frame(minWidth: 560, idealWidth: 640, minHeight: 520, idealHeight: 620)
            .onAppear {
                if store.isSigningIn {
                    selectedTab = "connection"
                }
            }
            .onChange(of: store.isSigningIn) {
                if store.isSigningIn {
                    selectedTab = "connection"
                }
            }
        #else
            NavigationStack {
                Form {
                    connectionSections
                    Section {
                        NavigationLink {
                            SettingsNotificationsView(notifications: store.notifications)
                        } label: {
                            Label("Notifications", systemImage: "bell")
                        }
                    }
                    generalSections
                    aboutSection
                    previewSection
                }
                .formStyle(.grouped)
                .navigationTitle("Connection")
                .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } } }
            }
        #endif
    }

    @ViewBuilder
    private var connectionSections: some View {
        Section("Connection") {
            Text(store.connectionStatus)
            if !store.isSample {
                if store.connectionPresentation.remote != store.connectionStatus {
                    Text(store.connectionPresentation.remote).font(.caption).foregroundStyle(.secondary)
                }
                if let receivedAt = store.rosterReceivedAt {
                    LabeledContent("Machine list received") {
                        Text(receivedAt, style: .relative)
                    }
                    .font(.caption).foregroundStyle(.secondary)
                }
            }
            if let cacheStatus = store.cacheStatus {
                Text(cacheStatus).font(.caption).foregroundStyle(.secondary)
            }
            Button("Reconnect", action: connect).disabled(store.isSigningIn || store.isFixture)
            if store.isLive || store.isConnecting {
                Button("Disconnect", action: store.disconnect).disabled(store.isFixture)
            }
        }
        #if os(macOS)
            if !store.isSample, !store.isFixture, let local = store.connectionPresentation.local {
                Section("Local host") {
                    Text(local)
                    Text("A local host is optional. Remote machines connect through the relay independently.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
        #endif
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
                    Button("Copy Code and Open GitHub") {
                        copy(code.userCode)
                        copiedCode = true
                        openURL(code.verificationUri)
                    }
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
    }

    @ViewBuilder
    private var generalSections: some View {
        Section("Reading") {
            Toggle("Show reasoning and tool history", isOn: $showDetails)
        }
        #if os(macOS)
            MacLoginItemView().disabled(store.isFixture)
        #endif
    }

    private var aboutSection: some View {
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
    }

    private var previewSection: some View {
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
            UIPasteboard.general.setItems([["public.utf8-plain-text": text]], options: [.localOnly: true, .expirationDate: Date().addingTimeInterval(TimeInterval(store.signInCode?.expiresIn ?? 300))])
        #endif
    }
}
