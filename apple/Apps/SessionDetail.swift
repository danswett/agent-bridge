import AgentBridgeAppleCore
import SwiftUI

struct SessionDetail: View {
    let session: BridgeSession
    var readOnlyReason: String?
    var machineName: String?
    var canAnswer = false
    var onLoadHistory: (@MainActor (String) async throws -> JSONValue)?
    var onLoadDelegate: (@MainActor (String, String) async throws -> JSONValue)?
    var onAskMainToStop: (@MainActor (String) -> Void)?

    @State private var historyExpanded = false
    @State private var fullSession: JSONValue?
    @AppStorage("bridge.reading.showDetails") private var showDetails = true
    @State private var selectedDelegate: DelegateSelection?
    @State private var finishedExpanded = false
    @State private var pendingStop: (@MainActor () -> Void)?

    private struct DelegateSelection: Identifiable {
        let id: String
        let load: (@MainActor (String, String) async throws -> JSONValue)?
        let ask: (@MainActor (String) -> Void)?
    }

    private var delegateGroups: BridgeDelegatePresentation.Groups {
        BridgeDelegatePresentation.groups(session.delegates)
    }

    var body: some View {
        Form {
            if let question = session.question {
                Section(session.waitingOn == nil ? "Main agent asks" : session.statusText) {
                    BridgeMarkdown(question["message"]?.string ?? "This session is waiting for an answer.")
                    Label(canAnswer ? "Use Answer to respond" : "Answer at the machine", systemImage: canAnswer ? "questionmark.bubble" : "desktopcomputer")
                        .font(.callout).foregroundStyle(.secondary)
                    if let readOnlyReason {
                        Text(readOnlyReason).font(.caption).foregroundStyle(.secondary)
                    }
                    if question["truncated"]?.bool == true {
                        Text("The complete question is available at the machine.").font(.caption)
                    }
                }
            }
            if let message = session.activity?["userMessage"]?.string, !message.isEmpty {
                Section("You") { Text(message) }
            }
            if session.response?.isEmpty == false || session.responseTruncated {
                Section("\(session.agent.capitalized) response") {
                    if let response = session.response, !response.isEmpty {
                        BridgeMarkdown(response)
                    }
                    if session.responseTruncated {
                        Text(session.response?.isEmpty == false ? "Earlier response text was omitted by the host." : "The main agent’s response was omitted by the host’s message limit.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            if showDetails, let reasoning = session.reasoning, !reasoning.isEmpty {
                Section { DisclosureGroup("Reasoning") { BridgeMarkdown(reasoning) } }
            }
            if let tool = session.activity?["currentTool"], tool != .null {
                Section("Current tool") {
                    Label(tool["name"]?.string ?? "Tool", systemImage: "terminal")
                        .font(.body.monospaced())
                    if let summary = tool["summary"]?.string {
                        Text(summary).foregroundStyle(.secondary)
                    }
                }
            }
            if SessionPresentation.backgroundSummary(session: session) != nil || !session.delegates.isEmpty || session.delegatesMore > 0 {
                Section("Background activity") {
                    if let summary = SessionPresentation.backgroundSummary(session: session) {
                        Text(summary).foregroundStyle(.secondary)
                    }
                    DelegateRows(entries: delegateGroups.active, all: session.delegates, onSelect: selectDelegate)
                    if !delegateGroups.finished.isEmpty {
                        DisclosureGroup("\(delegateGroups.finished.count) finished", isExpanded: $finishedExpanded) {
                            DelegateRows(entries: delegateGroups.finished, all: session.delegates, onSelect: selectDelegate)
                        }
                    }
                    if session.delegatesMore > 0 {
                        Text("\(session.delegatesMore) more delegates not listed by the host.").font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            let events = BridgeDelegatePresentation.events(session.delegates)
            if !events.isEmpty {
                Section("Delegate activity") {
                    ForEach(events) { event in
                        VStack(alignment: .leading, spacing: 4) {
                            if let id = event.delegateID {
                                Button(event.text) { selectDelegate(id) }
                            } else {
                                Text(event.text)
                            }
                            Text(SessionPresentation.timestamp(event.at)).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
            }
            if showDetails, let onLoadHistory {
                Section {
                    DisclosureGroup("Tool history", isExpanded: $historyExpanded) {
                        if historyExpanded {
                            SessionHistoryView(sessionID: session.id, updatedAt: session.updatedAt, onLoad: onLoadHistory) {
                                fullSession = $0
                            }
                            .id(session.id)
                        }
                    }
                }
            }
            Section("Details") {
                ForEach(SessionPresentation.details(session: session, fullSession: fullSession, machineName: machineName)) { detail in
                    LabeledContent(detail.label) {
                        Text(detail.value)
                            .fontDesign(detail.monospaced ? .monospaced : .default)
                            .multilineTextAlignment(.trailing)
                    }
                }
            }
        }
        .formStyle(.grouped)
        .textSelection(.enabled)
        .navigationTitle(session.title)
        .sheet(item: $selectedDelegate, onDismiss: {
            let action = pendingStop
            pendingStop = nil
            action?()
        }) { selection in
            DelegateInspector(session: session, delegateID: selection.id, onLoad: selection.load,
                              onSelect: { selectedDelegate = DelegateSelection(id: $0, load: selection.load, ask: selection.ask) },
                              onAskMainToStop: selection.ask != nil && onAskMainToStop != nil ? {
                                  pendingStop = { selection.ask?(selection.id) }
                                  selectedDelegate = nil
                              } : nil)
                .id(session.id + ":" + selection.id)
        }
        .onChange(of: session.id) {
            historyExpanded = false
            fullSession = nil
        }
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
    }

    private func selectDelegate(_ id: String) {
        guard session.delegates.contains(where: { $0.id == id }) else { return }
        selectedDelegate = DelegateSelection(id: id, load: onLoadDelegate, ask: onAskMainToStop)
    }
}
