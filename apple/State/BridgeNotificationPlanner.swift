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
    struct History: Sendable {
        private struct DelegateWait: Sendable {
            let fromTurn: Bool
            let responseBeforeWait: String?
        }

        private var waits: [String: [String: DelegateWait]] = [:]
        private(set) var sessions: [String: [String: BridgeSession]] = [:]

        mutating func plan(machineID: String, machineName: String, after: [BridgeSession], showPreviews: Bool = false,
                           initialSnapshot: Bool = false, capabilities: Set<String> = []) -> BridgeNotificationPlan
        {
            // HostStore retains its old snapshot across a disconnect. Only this
            // cleared-with-history snapshot may establish a wait's provenance.
            let before = Array((sessions[machineID] ?? [:]).values)
            sessions[machineID] = Dictionary(after.map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
            return BridgeNotificationPlanner.plan(machineID: machineID, machineName: machineName, before: before, after: after,
                                                  showPreviews: showPreviews, initialSnapshot: initialSnapshot, capabilities: capabilities, history: &self)
        }

        mutating func clear(machineID: String, sessionID: String? = nil) {
            if let sessionID {
                sessions[machineID]?.removeValue(forKey: sessionID)
                waits[machineID]?.removeValue(forKey: sessionID)
                if waits[machineID]?.isEmpty == true {
                    waits.removeValue(forKey: machineID)
                }
            } else {
                sessions.removeValue(forKey: machineID)
                waits.removeValue(forKey: machineID)
            }
        }

        fileprivate mutating func endings(machineID: String, previous: [String: BridgeSession], current: [String: BridgeSession]) -> Set<String> {
            var machineWaits = waits[machineID] ?? [:]
            var endings = Set<String>()
            for session in current.values {
                let old = previous[session.id]
                var wait = machineWaits[session.id]
                if wait == nil, let old, Self.isDelegateWait(old) {
                    // A client first seeing a wait cannot assume the main turn was running.
                    wait = DelegateWait(fromTurn: false, responseBeforeWait: old.response)
                }
                if Self.isDelegateWait(session), !Self.isDelegateWait(old) {
                    if let old {
                        wait = DelegateWait(fromTurn: old.status == "working" || (old.status == "waiting" && old.waitingOn == nil),
                                            responseBeforeWait: old.response)
                    } else if wait == nil {
                        wait = DelegateWait(fromTurn: false, responseBeforeWait: session.response)
                    }
                }
                if let old, old.status == "working" || (old.status == "waiting" && (old.waitingOn == nil || wait?.fromTurn == true || session.response != wait?.responseBeforeWait)) {
                    endings.insert(session.id)
                }
                machineWaits[session.id] = Self.isDelegateWait(session) ? wait : nil
            }
            machineWaits = machineWaits.filter { current[$0.key] != nil }
            waits[machineID] = machineWaits.isEmpty ? nil : machineWaits
            return endings
        }

        private static func isDelegateWait(_ session: BridgeSession?) -> Bool {
            session?.status == "waiting" && session?.waitingOn != nil
        }
    }

    public static func plan(machineID: String, machineName: String, before: [BridgeSession], after: [BridgeSession],
                            showPreviews: Bool = false, initialSnapshot: Bool = false, capabilities: Set<String> = []) -> BridgeNotificationPlan
    {
        var history = History()
        return plan(machineID: machineID, machineName: machineName, before: before, after: after,
                    showPreviews: showPreviews, initialSnapshot: initialSnapshot, capabilities: capabilities, history: &history)
    }

    static func plan(machineID: String, machineName: String, before: [BridgeSession], after: [BridgeSession],
                     showPreviews: Bool = false, initialSnapshot: Bool = false, capabilities: Set<String> = [], history: inout History) -> BridgeNotificationPlan
    {
        let previous = Dictionary(before.map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
        let current = Dictionary(after.map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
        let endings = history.endings(machineID: machineID, previous: previous, current: current)
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
                } else if !initialSnapshot, endings.contains(session.id), isFinished(session), session.response?.isEmpty == false {
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
