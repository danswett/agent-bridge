import AgentBridgeAppleCore
import CryptoKit
import Foundation

public struct BridgeSessionNotice: Sendable, Equatable, Identifiable {
    public enum Kind: String, Sendable { case question, finished }
    public let id: String
    public let machineID: String
    public let sessionID: String
    public let questionID: String?
    public let version: String
    public let kind: Kind
    public let title: String
    public let body: String
    public let sessionVersion: String?
    public let questionSnapshot: JSONValue?
    public let actions: [BridgeNoticeAction]
}

public struct BridgeNotificationPlan: Sendable, Equatable {
    public let notices: [BridgeSessionNotice]
    public let removeIDs: [String]
    public let waitingCount: Int
}

public enum BridgeNotificationPlanner {
    public static func plan(machineID: String, machineName: String, before: [BridgeSession], after: [BridgeSession],
                            showPreviews: Bool = false, initialSnapshot: Bool = false, capabilities: Set<String> = []) -> BridgeNotificationPlan
    {
        let previous = Dictionary(before.map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
        let current = Dictionary(after.map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
        var removeIDs: [String] = []
        var notices: [BridgeSessionNotice] = []
        for old in previous.values {
            if let question = pendingQuestion(old), pendingQuestion(current[old.id]) != question {
                removeIDs.append(identifier(machineID: machineID, sessionID: old.id, version: question, kind: .question))
            }
        }
        if !initialSnapshot || current.values.contains(where: { pendingQuestion($0) != nil }) {
            for session in current.values.sorted(by: { $0.id < $1.id }) {
                let old = previous[session.id]
                let question = pendingQuestion(session)
                let kind: BridgeSessionNotice.Kind
                let version: String
                if let question, initialSnapshot || question != pendingQuestion(old) {
                    kind = .question
                    version = question
                } else if !initialSnapshot, let old, ["working", "waiting"].contains(old.status), isFinished(session), session.response?.isEmpty == false {
                    kind = .finished
                    version = session.response ?? session.updatedAt ?? "finished"
                } else {
                    continue
                }
                let title = showPreviews ? clean(session.title, limit: 80) : "Agent Bridge"
                let body: String
                if showPreviews {
                    let detail = kind == .question ? session.question?["message"]?.string ?? "Waiting for your answer." : session.response ?? "Finished its current turn."
                    body = clean("\(machineName): \(detail)", limit: 200)
                } else {
                    body = kind == .question ? "A session is waiting for your answer." : "A session finished its current turn."
                }
                notices.append(BridgeSessionNotice(id: identifier(machineID: machineID, sessionID: session.id, version: version, kind: kind),
                                                   machineID: machineID, sessionID: session.id, questionID: question, version: version, kind: kind, title: title, body: body,
                                                   sessionVersion: session.updatedAt, questionSnapshot: session.question,
                                                   actions: BridgeNotificationActions.offered(session: session, kind: kind, showPreviews: showPreviews && (kind != .question || clean("\(machineName): \(session.question?["message"]?.string ?? "Waiting for your answer.")", limit: Int.max).count <= 200), capabilities: capabilities)))
            }
        }
        return BridgeNotificationPlan(notices: notices, removeIDs: removeIDs.sorted(), waitingCount: current.values.filter { pendingQuestion($0) != nil }.count)
    }

    public static func isCurrent(_ notice: BridgeSessionNotice, session: BridgeSession?) -> Bool {
        guard let session else { return false }
        if notice.kind == .question {
            return pendingQuestion(session) == notice.questionID
        }
        return isFinished(session) && (session.response ?? session.updatedAt ?? "finished") == notice.version
    }

    public static func pendingQuestion(_ session: BridgeSession?) -> String? {
        guard let session, session.status != "ending", let id = session.question?["id"]?.string, !id.isEmpty else { return nil }
        return id
    }

    private static func isFinished(_ session: BridgeSession) -> Bool {
        session.status == "idle" && session.question == nil && session.backgroundAgents == 0 && session.backgroundShells == 0
    }

    private static func identifier(machineID: String, sessionID: String, version: String, kind: BridgeSessionNotice.Kind) -> String {
        let identity = [machineID, sessionID, kind.rawValue, version].map { "\($0.utf8.count):\($0)" }.joined()
        return "agentbridge." + SHA256.hash(data: Data(identity.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    private static func clean(_ text: String, limit: Int) -> String {
        let scalars = text.unicodeScalars.map { CharacterSet.controlCharacters.contains($0) || CharacterSet.newlines.contains($0) ? " " : String($0) }.joined()
        let clean = scalars.split(whereSeparator: \.isWhitespace).joined(separator: " ")
        return clean.count > limit ? String(clean.prefix(limit - 1)) + "…" : clean
    }
}
