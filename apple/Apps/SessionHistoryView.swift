import AgentBridgeAppleCore
import SwiftUI

struct SessionHistoryView: View {
    let sessionID: String
    let onLoad: @MainActor (String) async throws -> JSONValue
    var onLoaded: @MainActor (JSONValue) -> Void = { _ in }

    @State private var tools: [SessionPresentation.Tool] = []
    @State private var isLoading = false
    @State private var hasLoaded = false
    @State private var error: String?
    @State private var refreshID = UUID()
    @State private var loadedAt: Date?

    private struct Request: Hashable {
        let sessionID: String
        let refreshID: UUID
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if isLoading {
                ProgressView("Loading tools…")
            }
            if let error {
                Text(error).foregroundStyle(.secondary)
            } else if hasLoaded, tools.isEmpty {
                Text("No tool calls yet.").foregroundStyle(.secondary)
            }
            ForEach(tools) { tool in
                VStack(alignment: .leading, spacing: 4) {
                    Label(tool.name, systemImage: symbol(tool.status)).font(.body.monospaced())
                    Text(tool.status.capitalized).font(.caption).foregroundStyle(.secondary)
                    if let summary = tool.summary {
                        Text(summary)
                    }
                    if let started = tool.startedAt {
                        Text(SessionPresentation.timestamp(started)).font(.caption).foregroundStyle(.secondary)
                    }
                }
                .accessibilityElement(children: .combine)
            }
            if hasLoaded, !tools.isEmpty {
                Text("Latest \(tools.count) tool calls · newest first").font(.caption).foregroundStyle(.secondary)
            }
            if let loadedAt {
                Text("Loaded \(SessionPresentation.timestamp(loadedAt))").font(.caption).foregroundStyle(.secondary)
            }
            if hasLoaded || error != nil {
                Button(error == nil ? "Refresh Tool History" : "Try Again") { refreshID = UUID() }
                    .disabled(isLoading)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .task(id: Request(sessionID: sessionID, refreshID: refreshID)) {
            await load()
        }
    }

    private func load() async {
        isLoading = true
        error = nil
        do {
            let result = try await onLoad(sessionID)
            try Task.checkCancellation()
            if result["missing"]?.bool == true {
                tools = []
                error = "This session has ended."
            } else if result["id"]?.string != sessionID {
                error = "The host returned details for a different session."
            } else {
                tools = SessionPresentation.toolHistory(in: result)
                onLoaded(result)
                loadedAt = .now
            }
            hasLoaded = true
            isLoading = false
        } catch {
            guard !Task.isCancelled else { return }
            self.error = "Couldn’t load tool history: \(error.localizedDescription)"
            isLoading = false
        }
    }

    private func symbol(_ status: String) -> String {
        switch status {
        case "running": "circle.dashed"
        case "failed": "xmark.circle"
        default: "checkmark.circle"
        }
    }
}
