import AgentBridgeAppleCore
import SwiftUI

struct SessionRow: View {
    let session: BridgeSession

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: symbol)
                .foregroundStyle(session.question != nil ? .orange : session.status == "working" ? .blue : .secondary)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 5) {
                Text(session.title).font(.headline).lineLimit(2)
                Text("\(session.agent.capitalized) · \(session.statusText)")
                    .font(.caption).foregroundStyle(.secondary)
                if session.driver == "agent" {
                    Label("Driven by an agent", systemImage: "diamond.fill")
                        .font(.caption).foregroundStyle(.purple)
                }
            }
        }
        .padding(.vertical, 5)
        .accessibilityElement(children: .combine)
    }

    private var symbol: String {
        if session.status == "ending" {
            return "circle.dotted"
        }
        if session.question != nil || session.status == "waiting" {
            return "exclamationmark.circle.fill"
        }
        if session.status == "working" {
            return "circle.dashed"
        }
        if session.backgroundAgents > 0 || session.backgroundShells > 0 {
            return "circle.hexagongrid"
        }
        return "circle"
    }
}
