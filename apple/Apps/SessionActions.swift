import AgentBridgeAppleCore
import SwiftUI

enum SessionActionPresentation {
    case sheet, composer, inspector
}

struct SessionActions: View {
    let sessionID: String
    let store: BridgeStore
    @Bindable var host: HostStore
    var presentation: SessionActionPresentation = .sheet

    var body: some View {
        SessionActionContent(sessionID: sessionID, store: store, host: host, presentation: presentation)
            .id(Entry(host: ObjectIdentifier(host), sessionID: sessionID))
    }

    private struct Entry: Hashable {
        let host: ObjectIdentifier
        let sessionID: String
    }
}

private struct SessionActionContent: View {
    let sessionID: String
    let store: BridgeStore
    @Bindable var host: HostStore
    let presentation: SessionActionPresentation
    @Environment(\.dismiss) private var dismiss
    @State private var values: [String: JSONValue] = [:]
    @State private var answerText = ""
    @State private var error: String?
    @State private var loadedQuestionID: String?
    @State private var stopConfirmation: BridgeStopConfirmation?
    @State private var stopConnection: UUID?
    @State private var uploading = false
    @State private var composerHeight: CGFloat = 160
    @FocusState private var replyFocused: Bool
    private var attachments: Binding<[(UUID, String)]> {
        Binding(get: { host.attachmentDrafts[sessionID] ?? [] }, set: { host.attachmentDrafts[sessionID] = $0 })
    }

    private var replyText: Binding<String> {
        Binding(get: { host.replyDrafts[sessionID] ?? "" }, set: { host.replyDrafts[sessionID] = $0 })
    }

    private var session: BridgeSession? {
        host.sessions.first { $0.id == sessionID }
    }

    private var question: BridgeQuestion? {
        session?.question.flatMap(BridgeQuestion.init)
    }

    private var slot: String {
        "session:\(sessionID)"
    }

    private var attempt: MutationAttempt? {
        host.attempts[slot]
    }

    private var busy: Bool {
        attempt?.isSending == true || uploading
    }

    var body: some View {
        Group {
            switch presentation {
            case .sheet: sheet
            case .composer: composer
            case .inspector: inspector
            }
        }
        .onAppear { resetQuestion(); removeSubmittedReplyAttachments() }
        .onChange(of: attempt?.result) { removeSubmittedReplyAttachments() }
        .onChange(of: question?.id) { resetQuestion() }
        .onChange(of: host.connectionID) { stopConfirmation = nil }
    }

    private var sheet: some View {
        NavigationStack {
            Form {
                Section {
                    Text(session?.title ?? "Session ended").font(.headline)
                    Text(host.name).foregroundStyle(.secondary)
                    if !host.isLive {
                        Label(host.status, systemImage: "network.slash")
                    }
                    if let reason = host.readOnlyReason {
                        Text(reason)
                    }
                }
                if let attempt {
                    receipt(attempt)
                }
                if let question {
                    questionSection(question)
                } else if host.can("sessions.reply"), session != nil {
                    Section("Reply") { replyControls }
                } else if session != nil {
                    Text(replyHold ?? "This machine does not offer remote replies.")
                }
                if host.can("sessions.stop"), session != nil {
                    Section("End session") { stopControls }
                }
                if let error {
                    Section { errorText(error) }
                }
            }
            .formStyle(.grouped)
            .navigationTitle(question == nil ? "Session actions" : "Answer question")
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() }.disabled(busy) } }
            .interactiveDismissDisabled(busy)
        }
        #if os(macOS)
        .frame(minWidth: 480, idealWidth: 600, minHeight: 480, idealHeight: 680)
        #endif
    }

    private var composer: some View {
        // One scroll surface keeps the submit control reachable even with ten files
        // or a long structured question. Measuring its content avoids reserving the
        // maximum height for an ordinary two-line draft.
        ScrollView {
            composerContent
                .onGeometryChange(for: CGFloat.self, of: { $0.size.height }) { composerHeight = $0 }
        }
        .scrollBounceBehavior(.basedOnSize)
        .frame(idealHeight: composerHeight, maxHeight: composerHeight)
    }

    private var composerContent: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let attempt {
                receipt(attempt).font(.callout)
            }
            if let question {
                questionSection(question)
            } else {
                replyControls
            }
            if let error {
                errorText(error)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding()
    }

    private var replyHold: String? {
        if let reason = host.readOnlyReason {
            return reason
        }
        if !host.isLive {
            return host.status
        }
        if session == nil {
            return "This session has ended."
        }
        if !host.can("sessions.reply") {
            return "This machine does not offer remote replies."
        }
        return nil
    }

    @ViewBuilder private var replyControls: some View {
        VStack(alignment: .leading, spacing: 12) {
            TextField("Message this session", text: replyText, axis: .vertical)
                .lineLimit(3 ... 8)
                .textFieldStyle(.plain)
                .font(.body)
                .focused($replyFocused)
                .accessibilityLabel("Reply message")
                .accessibilityIdentifier("reply-composer-input")
                .padding(.horizontal, 4)
                .padding(.top, 4)
            AttachmentComposer(store: store, host: host, attachments: attachments,
                               disabled: replyHold != nil || attempt?.isSending == true || attempt?.uncertain == true,
                               compact: true, onBusy: { uploading = $0 }) {
                composerStatus
                Spacer(minLength: 4)
                if let model = session?.model, !model.isEmpty {
                    Text(model)
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                        .help("Session model: \(model)")
                        .accessibilityLabel("Session model: \(model)")
                }
                sendReplyButton
            }
        }
        .padding(12)
        .background(.background, in: .rect(cornerRadius: 20))
        .overlay {
            RoundedRectangle(cornerRadius: 20)
                .strokeBorder(replyFocused ? Color.accentColor.opacity(0.6) : Color.primary.opacity(0.12), lineWidth: 1)
                .allowsHitTesting(false)
        }
        .shadow(color: .black.opacity(0.04), radius: 6, y: 2)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("reply-composer")
        if let reason = replyHold {
            Text(reason).font(.callout).foregroundStyle(.secondary)
        }
    }

    private var composerStatus: some View {
        Label(host.isLive ? (replyHold == nil ? session?.statusText ?? "Ended" : "Read-only") : "Offline",
              systemImage: replyHold == nil ? "circle" : "lock")
            .font(.caption)
            .foregroundStyle(.secondary)
            .lineLimit(1)
            .help(replyHold ?? "The current state reported by this session.")
    }

    private var sendDisabled: Bool {
        busy || attempt?.uncertain == true || replyHold != nil || replyText.wrappedValue.isEmpty && attachments.wrappedValue.isEmpty
    }

    private var sendReplyButton: some View {
        Button(action: sendReply) {
            Label("Send Reply", systemImage: "arrow.up")
                .labelStyle(.iconOnly)
                .font(.headline)
                .foregroundStyle(.white)
                .frame(width: 36, height: 36)
                .background(sendDisabled ? Color.secondary.opacity(0.35) : Color.blue, in: .circle)
                #if os(iOS)
                    .frame(minWidth: 44, minHeight: 44)
                #endif
                .contentShape(.circle)
        }
        .buttonStyle(.plain)
        .keyboardShortcut(.return, modifiers: .command)
        .disabled(sendDisabled)
        .help("Send reply (Command-Return)")
        .accessibilityIdentifier("reply-composer-send")
    }

    @ViewBuilder private var inspector: some View {
        Section("End main session") {
            stopControls
            if !host.can("sessions.stop") {
                Text(host.readOnlyReason ?? (host.isLive ? "This machine does not offer remote stop." : host.status))
                    .font(.caption).foregroundStyle(.secondary)
            }
            if let error {
                errorText(error)
            }
        }
        if let attempt, attempt.operation.op == "session.stop" {
            receipt(attempt, title: "End session result")
        }
    }

    @ViewBuilder private var stopControls: some View {
        if let confirmation = stopConfirmation, confirmation.expiresAt > Date(), stopConnection == host.connectionID {
            Text("The session is still running. Press Force End to confirm stopping it.")
            Button("Force End", role: .destructive) { stop(confirmation) }
                .disabled(busy || attempt?.uncertain == true || !host.can("sessions.stop") || session == nil)
        } else {
            Button("End Session", role: .destructive) { stop(nil) }
                .disabled(busy || attempt?.uncertain == true || !host.can("sessions.stop") || session == nil)
        }
    }

    private func errorText(_ message: String) -> some View {
        Text(message).foregroundStyle(.red).textSelection(.enabled)
    }

    private func receipt(_ attempt: MutationAttempt, title: String = "Last action") -> some View {
        Section(title) {
            if attempt.isSending {
                ProgressView("Waiting for the machine…")
            }
            if let result = attempt.result {
                Text(result["outcome"]?.string?.capitalized ?? "Result received").font(.headline)
                if let reason = result["reason"]?.string {
                    Text(reason)
                }
            }
            if let error = attempt.error {
                Text(error)
            }
            if attempt.uncertain {
                if attempt.result != nil {
                    Text("The machine could not confirm delivery. Check this session at the machine.").font(.caption)
                } else if attempt.canRetry {
                    Text("Check delivery with the same request while the original host process is running.").font(.caption)
                    Button("Check Delivery Again") { Task { await host.retry(slot) } }
                        .disabled(!host.isLive || host.readOnlyReason != nil || host.instance != attempt.hostInstance)
                } else {
                    Text("The safe retry window expired. Check this session at the machine.").font(.caption)
                }
            }
        }
    }

    private func questionSection(_ question: BridgeQuestion) -> some View {
        Section("Question") {
            Text(question.message).textSelection(.enabled)
            if let reason = host.readOnlyReason ?? question.readOnlyReason(capabilities: host.capabilities, sessionAgent: session?.agent ?? "unknown") {
                Text(reason).foregroundStyle(.secondary)
            } else {
                if question.fields.isEmpty {
                    TextField("Answer", text: $answerText, axis: .vertical).lineLimit(2 ... 8)
                } else {
                    ForEach(question.fields) { field in QuestionFieldEditor(field: field, value: binding(field.name)) }.id(question.id)
                }
                Button("Send Answer", systemImage: "paperplane", action: answer)
                    .keyboardShortcut(.return, modifiers: .command)
                    .disabled(busy || !host.isLive || attempt?.uncertain == true || loadedQuestionID != question.id || attempt?.operation.data["question"]?.string == question.id && attempt?.result?["outcome"]?.string == "delivered")
            }
        }
    }

    private func binding(_ name: String) -> Binding<JSONValue?> {
        Binding(get: { values[name] }, set: { values[name] = $0 })
    }

    private func resetQuestion() {
        loadedQuestionID = question?.id
        values = [:]
        for field in question?.fields ?? [] {
            values[field.name] = field.defaultValue
        }
        answerText = ""
        error = nil
    }

    private func answer() {
        do {
            guard !busy, host.isLive, attempt?.uncertain != true, let question, let session else { return }
            if let reason = host.readOnlyReason {
                throw BridgeOperationError(reason)
            }
            let operation = try question.answer(sessionID: sessionID, currentQuestionID: loadedQuestionID,
                                                values: values, text: question.fields.isEmpty ? answerText : nil, capabilities: host.capabilities, sessionAgent: session.agent)
            let connection = host.connectionID
            Task {
                guard host.connectionID == connection, self.question?.id == question.id else { return }
                await host.perform(operation, slot: slot)
            }
        } catch { self.error = error.localizedDescription }
    }

    private func sendReply() {
        do {
            guard !busy, replyHold == nil, attempt?.uncertain != true else { return }
            let operation = try BridgeSessionOperations.reply(sessionID: sessionID, text: replyText.wrappedValue, attachments: attachments.wrappedValue.map(\.0),
                                                              hasQuestion: session?.question != nil, capabilities: host.capabilities)
            let connection = host.connectionID
            Task {
                guard host.connectionID == connection, session != nil, session?.question == nil else { return }
                await host.perform(operation, slot: slot, consumesReplyDraft: true)
                guard host.attempts[slot]?.operation.requestID == operation.requestID else { return }
                removeSubmittedReplyAttachments()
            }
        } catch { self.error = error.localizedDescription }
    }

    private func removeSubmittedReplyAttachments() {
        guard let attempt, attempt.operation.op == "session.reply", let result = attempt.result,
              ["delivered", "unconfirmed"].contains(result["outcome"]?.string ?? "") else { return }
        let used = Set(attempt.operation.data["attachments"]?.array?.compactMap { $0["id"]?.string.flatMap(UUID.init(uuidString:)) } ?? [])
        host.attachmentDrafts[sessionID]?.removeAll { used.contains($0.0) }
    }

    private func stop(_ confirmation: BridgeStopConfirmation?) {
        do {
            guard !busy, host.can("sessions.stop"), attempt?.uncertain != true, session != nil else { return }
            if confirmation != nil, stopConnection != host.connectionID {
                return
            }
            let operation = try BridgeSessionOperations.stop(sessionID: sessionID, confirmation: confirmation, capabilities: host.capabilities)
            let connection = host.connectionID
            Task {
                guard host.connectionID == connection, session != nil else { return }
                await host.perform(operation, slot: slot)
                guard host.connectionID == connection, let attempt,
                      attempt.operation.requestID == operation.requestID else { return }
                if let result = attempt.result {
                    stopConfirmation = BridgeStopResult(json: result, sessionID: sessionID).confirmation
                    stopConnection = connection
                }
            }
        } catch { self.error = error.localizedDescription }
    }
}

private struct QuestionFieldEditor: View {
    let field: BridgeQuestionField
    @Binding var value: JSONValue?
    @State private var other = ""
    @State private var number = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(field.title).font(.headline)
            if let description = field.description {
                Text(description).font(.caption).foregroundStyle(.secondary)
            }
            switch field.kind {
            case .choice:
                ForEach(field.displayOptions) { option in
                    Button { value = .string(option.value) } label: {
                        Label(option.label, systemImage: value == .string(option.value) ? "largecircle.fill.circle" : "circle")
                    }.buttonStyle(.plain)
                }
                if field.allowOther {
                    otherField(multiple: false)
                }
            case .multi:
                ForEach(field.displayOptions) { option in
                    Toggle(option.label, isOn: Binding(get: { value?.array?.contains(.string(option.value)) == true }, set: { selected in
                        var values = value?.array ?? []
                        values.removeAll { $0 == .string(option.value) }
                        if selected {
                            values.append(.string(option.value))
                        }
                        value = .array(values)
                    }))
                }
                if field.allowOther {
                    otherField(multiple: true)
                }
            case .boolean:
                Picker(field.title, selection: Binding(get: { value?.bool.map { $0 ? "true" : "false" } ?? "" }, set: { value = $0.isEmpty ? nil : .bool($0 == "true") })) {
                    Text("Choose…").tag(""); Text("Yes").tag("true"); Text("No").tag("false")
                }
            case .number:
                TextField("Number", text: $number)
                    .onAppear { number = value?.number.map { String($0) } ?? "" }
                    .onChange(of: number) { value = Double(number).map(JSONValue.number) }
            case .text:
                TextField("Answer", text: Binding(get: { value?.string ?? "" }, set: { value = .string($0) }), axis: .vertical)
            }
        }.padding(.vertical, 4)
    }

    private func otherField(multiple: Bool) -> some View {
        TextField("Other answer", text: $other).onChange(of: other) {
            if multiple {
                var values = value?.array?.filter { $0["other"] == nil } ?? []
                if !other.isEmpty {
                    values.append(.object(["other": .string(other)]))
                }
                value = .array(values)
            } else {
                value = other.isEmpty ? nil : .object(["other": .string(other)])
            }
        }
    }
}
