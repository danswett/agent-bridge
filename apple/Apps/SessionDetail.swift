import AgentBridgeAppleCore
import SwiftUI

struct SessionDetail: View {
    let session: BridgeSession
    var readOnlyReason: String?
    var machineName: String?
    var onLoadHistory: (@MainActor (String) async throws -> JSONValue)?

    @State private var historyExpanded = false
    @State private var fullSession: JSONValue?

    var body: some View {
        Form {
            if let question = session.question {
                Section(session.waitingOn == nil ? "Main agent asks" : session.statusText) {
                    BridgeMarkdown(question["message"]?.string ?? "This session is waiting for an answer.")
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
                            SessionHistoryView(sessionID: session.id, onLoad: onLoadHistory) {
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

/// Native text rendering keeps HTML out of executable web content.
private struct BridgeMarkdown: View {
    private struct Block: Identifiable {
        enum Kind { case paragraph, heading, quote, code }
        let id: Int
        let kind: Kind
        let text: String
    }

    private let blocks: [Block]

    init(_ source: String) {
        var result: [Block] = []
        var paragraph: [String] = []
        var paragraphLine = 0
        var code: [String] = []
        var fence: String?
        var fenceLine = 0
        func flushParagraph() {
            guard !paragraph.isEmpty else { return }
            result.append(Block(id: paragraphLine, kind: .paragraph, text: paragraph.joined(separator: "\n")))
            paragraph = []
        }
        for (lineNumber, line) in source.components(separatedBy: .newlines).enumerated() {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if let marker = fence {
                if trimmed.hasPrefix(marker) {
                    result.append(Block(id: fenceLine, kind: .code, text: code.joined(separator: "\n")))
                    code = []
                    fence = nil
                } else {
                    code.append(line)
                }
                continue
            }
            if trimmed.hasPrefix("```") || trimmed.hasPrefix("~~~") {
                flushParagraph()
                fence = String(trimmed.prefix(3))
                fenceLine = lineNumber
            } else if trimmed.isEmpty {
                flushParagraph()
            } else if trimmed.hasPrefix("#"), let space = trimmed.firstIndex(of: " "),
                      trimmed[..<space].allSatisfy({ $0 == "#" }), trimmed.distance(from: trimmed.startIndex, to: space) <= 6
            {
                flushParagraph()
                result.append(Block(id: lineNumber, kind: .heading, text: String(trimmed[trimmed.index(after: space)...])))
            } else if trimmed.hasPrefix("> ") {
                flushParagraph()
                result.append(Block(id: lineNumber, kind: .quote, text: String(trimmed.dropFirst(2))))
            } else {
                if paragraph.isEmpty {
                    paragraphLine = lineNumber
                }
                paragraph.append(line)
            }
        }
        flushParagraph()
        if fence != nil {
            result.append(Block(id: fenceLine, kind: .code, text: code.joined(separator: "\n")))
        }
        blocks = result
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            ForEach(blocks) { block in
                switch block.kind {
                case .code:
                    ScrollView(.horizontal) {
                        Text(block.text).font(.body.monospaced()).padding(10)
                    }
                    .background(.quaternary, in: .rect(cornerRadius: 6))
                case .heading:
                    Text(inline(block.text)).font(.headline).accessibilityAddTraits(.isHeader)
                case .quote:
                    Text(inline(block.text)).foregroundStyle(.secondary).padding(.leading, 12)
                case .paragraph:
                    Text(inline(block.text))
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func inline(_ text: String) -> AttributedString {
        (try? AttributedString(markdown: text, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace))) ?? AttributedString(text)
    }
}
