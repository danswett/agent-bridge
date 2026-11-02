import AgentBridgeAppleCore
import SwiftUI

struct DelegateInspector: View {
    let session: BridgeSession
    let delegateID: String
    let onLoad: (@MainActor (String, String) async throws -> JSONValue)?
    let onSelect: (String) -> Void
    var onAskMainToStop: (() -> Void)?
    @Environment(\.dismiss) private var dismiss
    @AppStorage("bridge.reading.showDetails") private var showDetails = true
    @State private var loader = BridgeHistoryLoader(subject: .delegate)
    @State private var content = BridgeDelegateContent()
    @State private var needsRefresh = false
    @State private var lastRequested = Date.distantPast

    private var entry: BridgeDelegate? {
        session.delegates.first { $0.id == delegateID }
    }

    private var nested: [BridgeDelegatePresentation.Entry] {
        BridgeDelegatePresentation.descendants(of: delegateID, in: session.delegates)
    }

    var body: some View {
        NavigationStack {
            TimelineView(.periodic(from: .now, by: 10)) { clock in
                Form {
                    if let entry {
                        Section {
                            Text("Delegate").font(.caption).foregroundStyle(.secondary)
                            Label(entry.name, systemImage: entry.symbol).font(.title3)
                                .accessibilityLabel(BridgeDelegateLabel.titled(entry.name))
                            LabeledContent("Status", value: entry.statusText)
                            if let model = content.fetched?.model ?? entry.model {
                                LabeledContent("Model", value: model)
                            }
                            LabeledContent("Kind", value: entry.kind == "background" ? "Background" : "Task")
                            LabeledContent("ID", value: entry.id).fontDesign(.monospaced)
                            if let started = entry.startedAt {
                                LabeledContent("Started", value: SessionPresentation.timestamp(started))
                            }
                            if let ended = entry.endedAt {
                                LabeledContent("Finished", value: SessionPresentation.timestamp(ended))
                            }
                            if let duration = BridgeDelegatePresentation.duration(from: entry.startedAt, to: entry.endedAt ?? clock.date) {
                                LabeledContent("Duration", value: duration)
                            }
                        }
                        if !content.task.isEmpty {
                            Section("Task") { BridgeMarkdown(content.task) }
                        }
                        if !content.latest.isEmpty {
                            Section("Latest") {
                                BridgeMarkdown(content.latest)
                                if content.latestTruncated {
                                    Text("Earlier delegate response text was omitted by the host.").font(.caption).foregroundStyle(.secondary)
                                }
                            }
                        }
                        if showDetails, let reasoning = content.fetched?.reasoning, !reasoning.isEmpty {
                            Section { DisclosureGroup("Reasoning") { BridgeMarkdown(reasoning) } }
                        }
                        if showDetails {
                            let tools = BridgeDelegatePresentation.tools(content.fetched ?? entry, listed: entry)
                            if !tools.isEmpty {
                                Section("Recent tools") {
                                    ForEach(tools) { tool in
                                        VStack(alignment: .leading, spacing: 4) {
                                            Label(tool.name, systemImage: tool.status == "failed" ? "xmark.circle" : tool.status == "running" ? "circle.dashed" : "checkmark.circle")
                                                .font(.body.monospaced())
                                            if let summary = tool.summary {
                                                Text(summary).foregroundStyle(.secondary)
                                            }
                                            if let duration = BridgeDelegatePresentation.duration(from: tool.startedAt, to: tool.endedAt ?? clock.date) {
                                                Text("\(tool.status.capitalized) · \(duration)").font(.caption).foregroundStyle(.secondary)
                                            }
                                        }.accessibilityElement(children: .combine)
                                    }
                                }
                            }
                        }
                        if !nested.isEmpty {
                            Section("Nested delegates") { DelegateRows(entries: nested, all: session.delegates, now: clock.date, onSelect: onSelect) }
                        }
                        Section {
                            if loader.isLoading {
                                ProgressView("Loading delegate…")
                            }
                            if let error = loader.error {
                                Text(error).foregroundStyle(.secondary)
                            }
                            if let loaded = loader.loadedAt {
                                Text("Loaded \(SessionPresentation.timestamp(loaded))").font(.caption).foregroundStyle(.secondary)
                            }
                            if onLoad != nil {
                                Button("Refresh Delegate", action: refresh).disabled(loader.isLoading)
                            } else {
                                Text("Full delegate details require a connected host.").foregroundStyle(.secondary)
                            }
                        }
                        if let onAskMainToStop, entry.status != "ended", BridgeDelegateLabel.safeID(entry.id) != nil {
                            Section {
                                Button("Ask main agent to stop it", action: onAskMainToStop)
                                Text("This prepares a reply naming the delegate by ID. Review it and press Send; nothing is sent now.")
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                        }
                    } else {
                        ContentUnavailableView("Delegate no longer listed", systemImage: "person.crop.circle.badge.questionmark",
                                               description: Text("The host no longer includes this delegate in the session."))
                    }
                }
                .formStyle(.grouped)
                .textSelection(.enabled)
            }
            .navigationTitle("Delegate details")
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } } }
            .onAppear {
                if let entry {
                    content.update(entry); loader.activate(sessionID: delegateID); refresh()
                }
            }
            .onChange(of: entry) { old, entry in
                guard let entry else { loader.deactivate(); return }
                if old == nil {
                    loader.activate(sessionID: delegateID)
                }
                content.update(entry)
                needsRefresh = true
                refreshIfDue()
            }
            .task {
                while !Task.isCancelled {
                    do { try await Task.sleep(for: .seconds(2)) } catch { return }
                    refreshIfDue()
                }
            }
            .onDisappear { loader.deactivate() }
        }
        #if os(macOS)
        .frame(minWidth: 480, idealWidth: 620, minHeight: 500, idealHeight: 720)
        #endif
    }

    private func refreshIfDue() {
        guard let entry else { return }
        let elapsed = Date.now.timeIntervalSince(lastRequested)
        if elapsed >= 2, needsRefresh || loader.shouldRetry || entry.isRunning && elapsed >= 10 {
            refresh()
        }
    }

    private func refresh() {
        guard entry != nil, let onLoad else { return }
        lastRequested = .now
        needsRefresh = false
        let revision = content.revision
        loader.refresh(load: { id in
            try await BridgeDelegatePresentation.validateDetail(onLoad(session.id, id), expectedID: id)
        }, onValue: { value in
            if value["missing"]?.bool == true {
                content.clearFetched(); return
            }
            if let detail = BridgeDelegate(json: value) {
                content.accept(detail, revision: revision)
            }
        })
    }
}

struct DelegateRows: View {
    let entries: [BridgeDelegatePresentation.Entry]
    let all: [BridgeDelegate]
    var now = Date.now
    let onSelect: (String) -> Void

    var body: some View {
        ForEach(entries) { row in
            Button { onSelect(row.id) } label: {
                VStack(alignment: .leading, spacing: 4) {
                    Label(BridgeDelegateLabel.titled(row.delegate.name), systemImage: row.delegate.symbol).font(.headline)
                    HStack {
                        Text(row.delegate.statusText)
                        if let duration = BridgeDelegatePresentation.duration(from: row.delegate.startedAt, to: row.delegate.endedAt ?? now) {
                            Text(duration)
                        }
                        let count = BridgeDelegatePresentation.descendants(of: row.id, in: all).count
                        if count > 0 {
                            Text("\(count) nested")
                        }
                    }.font(.caption).foregroundStyle(.secondary)
                    if let parent = row.parentName {
                        Text("Under \(BridgeDelegateLabel.titled(parent))").font(.caption).foregroundStyle(.secondary)
                    }
                    if let line = row.delegate.line {
                        Text(line).lineLimit(2).foregroundStyle(.secondary)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.leading, CGFloat(min(row.depth, 3)) * 16)
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
            .accessibilityElement(children: .combine)
            .accessibilityHint("Open delegate details")
        }
    }
}
