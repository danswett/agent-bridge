import AgentBridgeAppleCore
import SwiftUI

struct SessionDetail: View {
    let session: BridgeSession
    var readOnlyReason: String?
    var machineName: String?
    var canAnswer = false
    var onLoadHistory: (@MainActor (String) async throws -> JSONValue)?

    @State private var historyExpanded = false
    @State private var fullSession: JSONValue?

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
            if let response = session.response, !response.isEmpty {
                Section("\(session.agent.capitalized) response") {
                    BridgeMarkdown(response)
                    if session.responseTruncated {
                        Text("Earlier response text was omitted by the host.").font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            if let reasoning = session.reasoning, !reasoning.isEmpty {
                Section { DisclosureGroup("Reasoning") { Text(reasoning) } }
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
            if !session.delegates.isEmpty {
                Section("Delegates") {
                    ForEach(session.delegates) { delegate in
                        VStack(alignment: .leading, spacing: 4) {
                            Text(delegate.name).font(.headline)
                            Text(delegate.status.capitalized).font(.caption).foregroundStyle(.secondary)
                            if let line = delegate.line {
                                Text(line)
                            }
                        }
                        .accessibilityElement(children: .combine)
                    }
                }
            }
            if let onLoadHistory {
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
        .onChange(of: session.id) {
            historyExpanded = false
            fullSession = nil
        }
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
    }
}
