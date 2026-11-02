import AgentBridgeAppleCore
import SwiftUI

struct LaunchView: View {
    @Bindable var store: BridgeStore
    @Environment(\.dismiss) private var dismiss
    @State private var machineID = ""
    @State private var options: BridgeLaunchOptions?
    @State private var draft = BridgeLaunchDraft()
    @State private var error: String?
    @State private var loading = false
    @State private var resumeID = ""
    @State private var trustQuestions: [BridgeTrustQuestion] = []
    @State private var transferChoices: [TransferChoice] = []
    @State private var transferID = ""
    @State private var loadGeneration = UUID()
    @State private var attachments: [(UUID, String)] = []
    @State private var attachmentScope: AttachmentScope?
    @State private var uploadingScope: AttachmentScope?
    @State private var submitting = false
    @State private var loadedMachineID: String?
    @State private var loadedDraftKey: String?
    private var host: HostStore? {
        store.hosts.first { $0.id == machineID }
    }

    private var launcher: BridgeLauncher? {
        options?.agents.first { $0.id == draft.agent }
    }

    private var actionHost: HostStore? {
        host.map { store.launchReceiptHost(for: $0) }
    }

    private var attempt: MutationAttempt? {
        actionHost?.attempts["launch"]
    }

    private var draftKey: String {
        host?.hello?["machine"]?["id"]?.string ?? machineID
    }

    private var attachmentHost: HostStore? {
        guard let host else { return nil }
        return transferID.isEmpty ? host : store.remoteHost(for: host)
    }

    private var desiredAttachmentScope: AttachmentScope? {
        guard let target = attachmentHost, target.isLive else { return nil }
        return AttachmentScope(hostID: target.id, connectionID: target.connectionID)
    }

    private var busy: Bool {
        uploadingScope != nil || submitting || attempt?.isSending == true
    }

    private struct AttachmentScope: Hashable {
        let hostID: String
        let connectionID: UUID
        var draftKey: String {
            "launch:\(connectionID.uuidString)"
        }
    }

    private var eligibleHosts: [HostStore] {
        store.canonicalHosts.filter { $0.can("sessions.launch") || $0.capabilities.contains("launch.trust") || $0.attempts["launch"] != nil || $0.id == machineID }
    }

    private struct TransferChoice: Identifiable {
        let id: String
        let source: HostStore
        let session: BridgeResumableSession
    }

    private var pendingTrust: [BridgeTrustQuestion] {
        var questions = trustQuestions
        if let raw = attempt?.result?["trust"], let question = BridgeTrustQuestion(json: raw), !questions.contains(where: { $0.id == question.id }) {
            questions.append(question)
        }
        return questions
    }

    var body: some View {
        NavigationStack {
            Form {
                Section("Machine") {
                    Picker("Launch on", selection: $machineID) {
                        Text("Choose a machine").tag("")
                        ForEach(eligibleHosts) { host in Text(host.name).tag(host.id) }
                    }
                    .disabled(busy)
                }
                if loading {
                    ProgressView("Loading launch options…")
                }
                if let options, let host {
                    if let busy = options.busy {
                        Text(busy).foregroundStyle(.secondary)
                    }
                    Section("Session") {
                        Picker("Start", selection: $resumeID) {
                            Text("New session").tag("")
                            ForEach(options.resumable) { session in Text("Resume: \(session.title)").tag(session.id) }
                        }.disabled(!transferID.isEmpty)
                        if !transferChoices.isEmpty {
                            Picker("Bring from another machine", selection: $transferID) {
                                Text("None").tag("")
                                ForEach(transferChoices) { choice in Text("\(choice.source.name): \(choice.session.title)").tag(choice.id) }
                            }
                            if !transferID.isEmpty {
                                Text("Copies this ended session to the chosen workspace and resumes it here. The original remains on its machine.").font(.caption)
                            }
                        }
                        Picker("Agent", selection: optionalBinding(\.agent)) {
                            Text("Choose an agent").tag("")
                            ForEach(options.agents.filter(\.supported)) { agent in Text(agent.label).tag(agent.id) }
                        }.disabled(!resumeID.isEmpty)
                        if resumeID.isEmpty {
                            Picker("Workspace", selection: optionalBinding(\.workspace)) {
                                Text("Choose a workspace").tag("")
                                ForEach(options.workspaces) { workspace in Text(workspace.label).tag(workspace.label) }
                            }
                        }
                        if let launcher {
                            choices("Profile", values: launcher.profiles, keyPath: \.profile)
                            choices("Model", values: launcher.models, keyPath: \.model)
                            choices("Effort", values: launcher.efforts, keyPath: \.effort)
                            choices("Context", values: launcher.contexts, keyPath: \.context)
                        }
                        Picker("Permissions", selection: optionalBinding(\.permissions)) {
                            Text("Machine default").tag(""); Text("Ask").tag("ask"); Text("Allow all").tag("allowAll")
                        }
                        if draft.permissions == "allowAll" {
                            Text("This lets the agent act without asking for each permission in the launched session.").font(.caption)
                        }
                        TextField("Opening message", text: $draft.prompt, axis: .vertical).lineLimit(3 ... 10)
                        if launcher?.attachments == true, let target = attachmentHost, let scope = desiredAttachmentScope {
                            AttachmentComposer(store: store, host: target, attachments: attachmentBinding(for: scope),
                                               disabled: submitting || attempt?.isSending == true || attempt?.uncertain == true)
                            { uploading in
                                if uploading, scope == attachmentScope, scope == desiredAttachmentScope {
                                    uploadingScope = scope
                                } else if !uploading, uploadingScope == scope {
                                    uploadingScope = nil
                                }
                            }
                            .id(scope)
                        }
                        Button(!transferID.isEmpty ? "Copy and Resume" : resumeID.isEmpty ? "Launch Session" : "Resume Session", systemImage: "play.fill") { launch(host, options: options) }
                            .disabled(busy || attachmentHost?.can("sessions.launch") != true || attempt?.uncertain == true)
                    }
                    .disabled(busy)
                    if let attempt {
                        Section("Launch result") {
                            if attempt.isSending {
                                ProgressView("Starting session…")
                            }
                            if let result = attempt.result {
                                Text(result["outcome"]?.string?.capitalized ?? "Result received").font(.headline)
                                if let reason = result["reason"]?.string {
                                    Text(reason)
                                }
                                if let session = result["session"]?.string {
                                    LabeledContent("Session", value: session).textSelection(.enabled)
                                    Button("Open Session") { store.openSession(session, on: actionHost ?? host); dismiss() }
                                }
                                if result["trust"] != nil, result["trust"] != .null {
                                    Text("The launched session is waiting for folder trust. Review the full folder warning before continuing.")
                                }
                            }
                            if let error = attempt.error {
                                Text(error)
                            }
                            if attempt.uncertain {
                                Button("Check Launch Again") {
                                    if let actionHost {
                                        Task { await actionHost.retry("launch") }
                                    }
                                }
                                .disabled(actionHost?.isLive != true || actionHost?.instance != attempt.hostInstance || !attempt.canRetry)
                                Text("The same launch identity is reused. A host restart prevents retrying an uncertain launch.").font(.caption)
                            }
                        }
                    }
                    ForEach(pendingTrust) { question in FolderTrustView(question: question, host: host, store: store) }
                    Button("Refresh Launch Options") { Task { await loadOptions() } }.disabled(loading || busy)
                }
                if let error {
                    Section { Text(error).foregroundStyle(.red) }
                }
            }
            .formStyle(.grouped)
            .navigationTitle("Launch or Resume")
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() }.disabled(busy) } }
            .interactiveDismissDisabled(busy)
            .onAppear {
                if machineID.isEmpty {
                    machineID = eligibleHosts.first?.id ?? ""
                }
            }
            .task(id: machineID) { await loadOptions() }
            .onChange(of: desiredAttachmentScope) { synchronizeAttachments() }
            .onChange(of: attempt?.result, initial: true) { removeSubmittedAttachments() }
            .onChange(of: resumeID) {
                draft.resume = resumeID.isEmpty ? nil : resumeID
                if let resumed = options?.resumable.first(where: { $0.id == resumeID }) {
                    draft.workspace = nil; draft.agent = resumed.launcher
                } else {
                    draft.workspace = options?.defaults?["workspace"]?.string ?? options?.workspaces.first?.label
                }
            }
            .onChange(of: draft) {
                if !loading, loadedMachineID == machineID, loadedDraftKey == draftKey, !machineID.isEmpty {
                    store.launchDrafts[draftKey] = draft
                }
            }
            .onChange(of: transferID) {
                synchronizeAttachments()
                if let choice = transferChoices.first(where: { $0.id == transferID }) {
                    resumeID = ""; draft.resume = nil
                    draft.agent = options?.agents.first { $0.kind == choice.session.agent }?.id
                    draft.workspace = options?.defaults?["workspace"]?.string ?? options?.workspaces.first?.label
                }
            }
            .onChange(of: launcher?.id) { clearUnsupportedAttachments() }
        }
        #if os(macOS)
        .frame(minWidth: 520, idealWidth: 640, minHeight: 520, idealHeight: 720)
        #endif
    }

    private func optionalBinding(_ keyPath: WritableKeyPath<BridgeLaunchDraft, String?>) -> Binding<String> {
        Binding(get: { draft[keyPath: keyPath] ?? "" }, set: {
            if keyPath == \.agent, draft.agent != $0 {
                draft.profile = nil; draft.model = nil; draft.effort = nil; draft.context = nil
            }
            draft[keyPath: keyPath] = $0.isEmpty ? nil : $0
        })
    }

    private func attachmentBinding(for scope: AttachmentScope) -> Binding<[(UUID, String)]> {
        Binding(get: {
            attachmentScope == scope && desiredAttachmentScope == scope ? attachments : []
        }, set: { value in
            // A cancelled import can finish after its view has been replaced. It must never
            // append an old endpoint's IDs to the newly selected machine's draft.
            guard attachmentScope == scope, desiredAttachmentScope == scope else { return }
            replaceAttachments(value, scope: scope)
        })
    }

    private func replaceAttachments(_ value: [(UUID, String)], scope: AttachmentScope) {
        guard attachmentScope == scope, desiredAttachmentScope == scope else { return }
        attachments = value
        draft.attachments = value.map(\.0)
        store.hosts.first { $0.id == scope.hostID }?.attachmentDrafts[scope.draftKey] = value
    }

    private func synchronizeAttachments() {
        let next = desiredAttachmentScope
        guard attachmentScope != next else {
            draft.attachments = attachments.map(\.0)
            return
        }
        attachmentScope = next
        uploadingScope = nil
        attachments = []
        draft.attachments = []
        if let next, let target = attachmentHost {
            // Local pipe and relay attachments have different owners, even on the same
            // machine. A reconnect also requires a fresh staging scope.
            attachments = target.attachmentDrafts[next.draftKey] ?? []
            draft.attachments = attachments.map(\.0)
        }
        removeSubmittedAttachments()
    }

    private func clearUnsupportedAttachments() {
        guard !loading, options != nil, launcher?.attachments != true, !attachments.isEmpty,
              let scope = attachmentScope else { return }
        replaceAttachments([], scope: scope)
        error = "Attachments were cleared because this agent cannot receive them at launch."
    }

    private func removeSubmittedAttachments(on completedHost: HostStore? = nil) {
        guard let target = completedHost ?? actionHost, let attempt = target.attempts["launch"], let result = attempt.result,
              !["busy", "refused"].contains(result["outcome"]?.string ?? "") else { return }
        let used = Set(attempt.operation.data["attachments"]?.array?.compactMap { $0["id"]?.string.flatMap(UUID.init(uuidString:)) } ?? [])
        guard !used.isEmpty else { return }
        // A started process consumes its files even if it later fails to register. Keep
        // the immutable attempt for receipt checking, but never reuse its IDs in a new launch.
        for key in Array(target.attachmentDrafts.keys) where key.hasPrefix("launch:") {
            target.attachmentDrafts[key]?.removeAll { used.contains($0.0) }
        }
        if let scope = attachmentScope, scope.hostID == target.id, scope == desiredAttachmentScope {
            replaceAttachments(attachments.filter { !used.contains($0.0) }, scope: scope)
        }
    }

    @ViewBuilder private func choices(_ title: String, values: [String], keyPath: WritableKeyPath<BridgeLaunchDraft, String?>) -> some View {
        if !values.isEmpty {
            Picker(title, selection: optionalBinding(keyPath)) {
                Text("Default").tag("")
                ForEach(values, id: \.self) { Text($0).tag($0) }
            }
        }
    }

    private func loadOptions() async {
        guard uploadingScope == nil, !submitting else { return }
        let generation = UUID()
        loadGeneration = generation
        loadedMachineID = nil
        loadedDraftKey = nil
        options = nil; draft = BridgeLaunchDraft(); resumeID = ""; transferID = ""; transferChoices = []; trustQuestions = []; error = nil; loading = false
        synchronizeAttachments()
        guard let host else { return }
        loading = true
        let selected = machineID
        let selectedDraftKey = draftKey
        let saved = store.launchDrafts[selectedDraftKey]
        do {
            let operation = BridgeOperation(op: "launch.options")
            let result = try await Task { try await host.call(operation.op, data: operation.data, timeout: .seconds(90)) }.value
            guard generation == loadGeneration, selected == machineID, !Task.isCancelled else { return }
            let parsed = BridgeLaunchOptions(json: result)
            options = parsed
            draft.agent = parsed.defaults?["agent"]?.string ?? parsed.agents.first(where: \.supported)?.id
            draft.workspace = parsed.defaults?["workspace"]?.string ?? parsed.workspaces.first?.label
            if let saved {
                draft = saved; resumeID = saved.resume ?? ""
            }
            draft.attachments = attachments.map(\.0)
            trustQuestions = result["trust"]?.array?.compactMap(BridgeTrustQuestion.init) ?? []
            if let via = store.remoteHost(for: host) {
                if host.isLocal {
                    let remote = try await Task { try await via.call("launch.options", timeout: .seconds(90)) }.value
                    guard generation == loadGeneration, selected == machineID, !Task.isCancelled else { return }
                    trustQuestions = remote["trust"]?.array?.compactMap(BridgeTrustQuestion.init) ?? trustQuestions
                }
                if via.can("sessions.transfer") {
                    for source in store.hosts where !source.isLocal && source.id != via.id && source.can("sessions.transfer.serve") {
                        let sourceOptions = try? await Task { try await source.call("launch.options", timeout: .seconds(90)) }.value
                        guard generation == loadGeneration, selected == machineID, !Task.isCancelled else { return }
                        if let sourceOptions {
                            transferChoices += BridgeLaunchOptions(json: sourceOptions).resumable.map {
                                TransferChoice(id: source.id + ":" + $0.id, source: source, session: $0)
                            }
                        }
                    }
                }
            }
        } catch {
            if generation == loadGeneration, selected == machineID {
                self.error = error.localizedDescription
            }
        }
        if generation == loadGeneration, selected == machineID {
            loading = false
            if options != nil {
                loadedMachineID = selected
                loadedDraftKey = selectedDraftKey
                clearUnsupportedAttachments()
                if selectedDraftKey == draftKey {
                    store.launchDrafts[selectedDraftKey] = draft
                }
            }
        }
    }

    private func launch(_ host: HostStore, options: BridgeLaunchOptions) {
        guard !busy, attempt?.uncertain != true else { return }
        do {
            let operation: BridgeOperation
            let target: HostStore
            guard attachments.isEmpty || attachmentScope == desiredAttachmentScope else {
                throw BridgeOperationError("The attachment connection changed. Attach the files again.")
            }
            var submittedDraft = draft
            submittedDraft.attachments = attachments.map(\.0)
            if !transferID.isEmpty {
                guard let choice = transferChoices.first(where: { $0.id == transferID }),
                      let via = store.remoteHost(for: host), let peer = choice.source.peerKey, choice.source.isLive else { throw RelayError.disconnected }
                target = via
                operation = try BridgeTransfer.launch(draft: submittedDraft, sourceSession: choice.session, sourcePublicKey: peer,
                                                      targetMachineID: via.id, signerPrivateKey: store.signingKey(for: choice.source), options: options,
                                                      capabilities: via.capabilities, sourceCapabilities: choice.source.capabilities, isLocal: false)
            } else {
                target = host
                operation = try submittedDraft.operation(options: options, capabilities: host.capabilities)
            }
            let connection = target.connectionID
            submitting = true
            Task {
                defer { submitting = false }
                guard target.isLive, target.connectionID == connection else {
                    error = "The machine connection changed. Review the launch before trying again."
                    return
                }
                await store.performLaunch(operation, on: target)
                removeSubmittedAttachments(on: target)
            }
        } catch { self.error = error.localizedDescription }
    }
}
