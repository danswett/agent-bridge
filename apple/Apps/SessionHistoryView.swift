import AgentBridgeAppleCore
import SwiftUI

struct SessionHistoryView: View {
    let sessionID: String
    var updatedAt: String?
    let connectionID: UUID?
    let onLoad: @MainActor (String) async throws -> JSONValue
    var onLoaded: @MainActor (JSONValue) -> Void = { _ in }

    @State private var loader = BridgeHistoryLoader()
    private var tools: [SessionPresentation.Tool] {
        loader.value.map(SessionPresentation.toolHistory) ?? []
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if loader.isLoading, connectionID != nil {
                ProgressView("Loading tools…")
            }
            if connectionID == nil {
                Text("Tool history will refresh when the host reconnects.").foregroundStyle(.secondary)
            }
            if let error = loader.error {
                Text(error).foregroundStyle(.secondary)
            } else if loader.hasLoaded, tools.isEmpty {
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
            if loader.hasLoaded, !tools.isEmpty {
                Text("Latest \(tools.count) tool calls · newest first").font(.caption).foregroundStyle(.secondary)
            }
            if let loadedAt = loader.loadedAt {
                Text("Loaded \(SessionPresentation.timestamp(loadedAt))").font(.caption).foregroundStyle(.secondary)
            }
            if loader.hasLoaded || loader.error != nil {
                Button(loader.error == nil ? "Refresh Tool History" : "Try Again") { loader.refresh(load: onLoad) }
                    .disabled(loader.isLoading || connectionID == nil)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .onAppear {
            loader.activate(sessionID: sessionID)
            loader.connectionChanged(connectionID, load: onLoad)
        }
        .onChange(of: sessionID) {
            loader.activate(sessionID: sessionID)
            loader.connectionChanged(connectionID, load: onLoad)
        }
        .onChange(of: connectionID) { loader.connectionChanged(connectionID, load: onLoad) }
        .onChange(of: updatedAt) {
            if connectionID != nil { loader.refresh(load: onLoad) }
        }
        .onChange(of: loader.value) {
            if let value = loader.value {
                onLoaded(value)
            }
        }
        .onDisappear { loader.deactivate() }
    }

    private func symbol(_ status: String) -> String {
        switch status {
        case "running": "circle.dashed"
        case "failed": "xmark.circle"
        default: "checkmark.circle"
        }
    }
}
