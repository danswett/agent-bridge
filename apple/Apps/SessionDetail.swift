import AgentBridgeAppleCore
import SwiftUI

struct SessionDetail: View {
    let session: BridgeSession
    let readOnlyReason: String?

    var body: some View {
        Form {
            Section {
                LabeledContent("Agent", value: session.agent.capitalized)
                LabeledContent("Status", value: session.statusText)
                if let model = session.model {
                    LabeledContent("Model", value: model)
                }
                if let repository = session.repository {
                    LabeledContent("Repository", value: repository)
                }
                if let branch = session.branch {
                    LabeledContent("Branch", value: branch)
                }
                if let cwd = session.cwd {
                    LabeledContent("Working directory", value: cwd)
                }
            }
            if let question = session.question {
                Section("Needs you") {
                    Text(question["message"]?.string ?? "This session is waiting for an answer.")
                    Label("Answer at the machine", systemImage: "desktopcomputer")
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
                    Text(response)
                    if session.responseTruncated {
                        Text("Earlier response text was omitted by the host.").font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            if let reasoning = session.reasoning, !reasoning.isEmpty {
                Section { DisclosureGroup("Reasoning") { Text(reasoning) } }
            }
            if let tool = session.activity?["currentTool"] {
                Section("Current tool") {
                    Text(tool["name"]?.string ?? "Tool")
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
                    }
                }
            }
            Section("Details") { LabeledContent("Session ID", value: session.id) }
        }
        .formStyle(.grouped)
        .textSelection(.enabled)
        .navigationTitle(session.title)
        #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
        #endif
    }
}
