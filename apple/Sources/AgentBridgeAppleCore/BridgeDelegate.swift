import Foundation

public struct BridgeDelegate: Identifiable, Sendable, Equatable {
    public let id: String
    public let name: String
    public let status: String
    public let line: String?
    public let kind: String
    public let parent: String?
    public let outcome: String?
    public let task: String?
    public let model: String?
    public let startedAt: Date?
    public let endedAt: Date?
    public let currentTool: JSONValue?
    public let response: String?
    public let responseTruncated: Bool
    public let reasoning: String?
    public let history: [JSONValue]

    public init?(json: JSONValue) {
        guard let id = json["id"]?.string, !id.isEmpty else { return nil }
        self.id = id
        name = BridgeDelegateLabel.label(name: json["name"]?.string, id: id)
        let status = json["status"]?.string ?? "idle"
        self.status = ["working", "waiting", "idle", "ended"].contains(status) ? status : "idle"
        line = json["line"]?.string
        kind = json["kind"]?.string == "background" ? "background" : "task"
        parent = json["parent"]?.string
        outcome = json["outcome"]?.string
        task = json["task"]?.string
        model = json["model"]?.string
        startedAt = SessionPresentation.date(json["startedAt"]?.string)
        endedAt = SessionPresentation.date(json["endedAt"]?.string)
        currentTool = json["currentTool"] == .null ? nil : json["currentTool"]
        response = json["response"]?.string
        responseTruncated = json["responseTruncated"]?.bool ?? false
        reasoning = json["reasoning"]?.string
        history = json["history"]?.array ?? []
    }

    public var isRunning: Bool {
        status == "working" || status == "waiting"
    }

    public var statusText: String {
        switch status {
        case "working": "Working"
        case "waiting": "Asks"
        case "ended": outcome == "failed" ? "Failed" : outcome == "done" ? "Done" : "Ended"
        default: "Idle"
        }
    }

    public var symbol: String {
        switch status {
        case "working": "circle.dashed"
        case "waiting": "exclamationmark.circle.fill"
        case "ended": outcome == "failed" ? "xmark.circle" : outcome == "done" ? "checkmark.circle" : "stop.circle"
        default: "circle"
        }
    }
}
