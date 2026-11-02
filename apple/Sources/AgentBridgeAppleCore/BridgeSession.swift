import Foundation

public struct BridgeSession: Identifiable, Sendable, Equatable {
    public let id: String
    public let agent: String
    public let status: String
    public let title: String
    public let cwd: String?
    public let repository: String?
    public let branch: String?
    public let model: String?
    public let updatedAt: String?
    public let driver: String
    public let activity: JSONValue?
    public let question: JSONValue?
    public let delegates: [BridgeDelegate]
    public let waitingOn: String?

    public init?(json: JSONValue) {
        guard let id = json["id"]?.string, !id.isEmpty else { return nil }
        self.id = id
        let agent = json["agent"]?.string ?? "unknown"
        self.agent = ["copilot", "claude", "codex"].contains(agent) ? agent : "unknown"
        let status = json["status"]?.string ?? "idle"
        self.status = ["working", "waiting", "idle", "ending"].contains(status) ? status : "idle"
        cwd = json["cwd"]?.string
        title = [json["title"]?.string, cwd, id].compactMap(\.self).first { !$0.isEmpty } ?? id
        repository = json["repository"]?.string
        branch = json["branch"]?.string
        model = json["model"]?.string
        updatedAt = json["updatedAt"]?.string
        driver = json["driver"]?.string == "agent" ? "agent" : "person"
        activity = json["activity"]
        question = json["question"] == .null ? nil : json["question"]
        var seen = Set<String>()
        delegates = (json["subagents"]?.array ?? []).compactMap(BridgeDelegate.init(json:)).filter { seen.insert($0.id).inserted }
        waitingOn = json["waitingOn"]?.string ?? question?["agent"]?.string
    }

    /// A delegate's latest text must never replace the main agent's conversation.
    public var response: String? {
        ownText("response")
    }

    public var reasoning: String? {
        ownText("reasoning")
    }

    public var responseTruncated: Bool {
        if activity?["source"]?["response"]?.string != nil {
            return activity?["own"]?["responseTruncated"]?.bool ?? false
        }
        return activity?["responseTruncated"]?.bool ?? activity?["own"]?["responseTruncated"]?.bool ?? false
    }

    private func ownText(_ key: String) -> String? {
        let own = activity?["own"]?[key]?.string
        if activity?["source"]?[key]?.string != nil {
            return own
        }
        return activity?[key]?.string ?? own
    }

    public var backgroundAgents: Int {
        count(activity?["background"]?["agents"])
    }

    public var backgroundShells: Int {
        count(activity?["background"]?["shells"])
    }

    private func count(_ value: JSONValue?) -> Int {
        guard let value = value?.number, value.isFinite, value > 0 else { return 0 }
        return Int(min(value, 1_000_000))
    }

    public var statusText: String {
        if status == "ending" {
            return "Ending session…"
        }
        if question != nil {
            if let waitingOn {
                let name = delegates.first { $0.id == waitingOn }?.name ?? "A delegate"
                return "\(name) asks"
            }
            return "Waiting on you"
        }
        if status == "working" {
            return "Working"
        }
        if status == "waiting" {
            return "Waiting on you"
        }
        if backgroundAgents > 0 {
            return "Delegating to \(backgroundAgents) \(backgroundAgents == 1 ? "agent" : "agents")"
        }
        if backgroundShells > 0 {
            return "\(backgroundShells) background \(backgroundShells == 1 ? "command" : "commands")"
        }
        return "Idle"
    }
}
