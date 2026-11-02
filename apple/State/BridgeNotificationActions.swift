import AgentBridgeAppleCore
import Foundation

public struct BridgeNoticeAction: Sendable, Equatable, Identifiable {
    public enum Kind: Sendable, Equatable {
        case choice(field: String, value: String)
        case answerText
        case reply
    }

    public let id: String
    public let title: String
    public let kind: Kind
}

public struct BridgeNoticeActionRequest: Sendable {
    public let notice: BridgeSessionNotice
    public let action: BridgeNoticeAction
    public let scope: String
    public let text: String?
    public let requestID: UUID
}

/// Notification arguments never become an operation directly. Both the current
/// subscription and a fresh session.get must still match the displayed notice.
public enum BridgeNotificationActions {
    public static func offered(session: BridgeSession, kind: BridgeSessionNotice.Kind, showPreviews: Bool,
                               capabilities: Set<String>) -> [BridgeNoticeAction]
    {
        if kind == .finished {
            return capabilities.contains("sessions.reply") ? [.init(id: "reply", title: "Reply", kind: .reply)] : []
        }
        // Option labels and one-click approvals need the question visible. Keeping
        // previews off never places private choices or a blind approval on the lock screen.
        guard showPreviews, let raw = session.question, let question = BridgeQuestion(json: raw),
              question.readOnlyReason(capabilities: capabilities, sessionAgent: session.agent) == nil else { return [] }
        if question.fields.isEmpty, !question.isApproval {
            return [.init(id: "answer", title: "Answer", kind: .answerText)]
        }
        guard question.fields.count == 1, let field = question.fields.first, field.kind == .choice,
              !field.allowOther, (1 ... 3).contains(field.options.count),
              Set(field.options.map(\.value)).count == field.options.count,
              field.options.allSatisfy({ !$0.label.isEmpty && $0.label.count <= 50 && !$0.label.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains) }) else { return [] }
        if question.isApproval {
            guard field.name == "decision", Set(field.options.map(\.value)) == ["approve", "deny"] else { return [] }
        }
        return field.displayOptions.enumerated().map { index, option in
            .init(id: "choice.\(index)", title: question.isApproval ? (option.value == "approve" ? "Approve Once" : "Deny") : option.label,
                  kind: .choice(field: field.name, value: option.value))
        }
    }

    public static func operation(_ request: BridgeNoticeActionRequest, current: BridgeSession, scope: String,
                                 capabilities: Set<String>, isLive: Bool) throws -> BridgeOperation
    {
        guard isLive, request.scope == scope, current.id == request.notice.sessionID,
              request.notice.actions.contains(request.action),
              BridgeNotificationPlanner.isCurrent(request.notice, session: current),
              current.updatedAt == request.notice.sessionVersion
        else {
            throw BridgeOperationError("This notification is no longer current. Review the session before sending.")
        }
        switch request.action.kind {
        case .reply:
            guard request.notice.kind == .finished, current.question == nil else { throw BridgeOperationError("The session is now waiting on a question.") }
            return try BridgeSessionOperations.reply(sessionID: current.id, text: request.text ?? "", hasQuestion: false,
                                                     capabilities: capabilities, requestID: request.requestID)
        case .choice, .answerText:
            guard current.question == request.notice.questionSnapshot, let raw = current.question,
                  let question = BridgeQuestion(json: raw) else { throw BridgeOperationError("The question changed. Review it in the session.") }
            let values: [String: JSONValue]
            let text: String?
            if case let .choice(field, value) = request.action.kind {
                values = [field: .string(value)]; text = nil
            } else {
                values = [:]; text = request.text
            }
            return try question.answer(sessionID: current.id, currentQuestionID: request.notice.questionID, values: values, text: text,
                                       capabilities: capabilities, sessionAgent: current.agent, requestID: request.requestID)
        }
    }
}

/// One delivered notice authorizes at most one explicit press in this process.
/// Only opaque tokens travel through Notification Center; its payload is not trusted
/// to recreate an answer, a request UUID, or an earlier account's authorization.
struct BridgeNoticeActionRegistry {
    struct Binding {
        let token: UUID
        let scope: String
        let requestID: UUID
        let notice: BridgeSessionNotice
    }

    private(set) var bindings: [String: Binding] = [:]

    mutating func register(_ notice: BridgeSessionNotice, scope: String) {
        guard !notice.actions.isEmpty, bindings.count < 64 else { return }
        bindings[notice.id] = Binding(token: UUID(), scope: scope, requestID: UUID(), notice: notice)
    }

    mutating func remove(_ id: String) {
        bindings.removeValue(forKey: id)
    }

    mutating func consume(identifier: String, token: String?, machine: String?, session: String?, action: String, text: String?) -> BridgeNoticeActionRequest? {
        guard let binding = bindings[identifier], token == binding.token.uuidString,
              machine == binding.notice.machineID, session == binding.notice.sessionID,
              let selected = binding.notice.actions.first(where: { $0.id == action }) else { return nil }
        bindings.removeValue(forKey: identifier)
        return BridgeNoticeActionRequest(notice: binding.notice, action: selected, scope: binding.scope, text: text, requestID: binding.requestID)
    }
}

@MainActor
enum BridgeNotificationDelivery {
    static func perform(_ request: BridgeNoticeActionRequest, host: HostStore, scope: String,
                        isCurrentContext: @MainActor () -> Bool) async -> String
    {
        let notSent = "Not sent. Review the current session before sending."
        let slot = "session:\(request.notice.sessionID)"
        let connection = host.connectionID
        guard isCurrentContext(), host.isLive, host.readOnlyReason == nil,
              host.attempts[slot]?.isSending != true, host.attempts[slot]?.uncertain != true,
              let current = host.sessions.first(where: { $0.id == request.notice.sessionID }) else { return notSent }
        do {
            _ = try BridgeNotificationActions.operation(request, current: current, scope: scope, capabilities: host.capabilities, isLive: host.isLive)
            let raw = try await host.call("session.get", data: ["id": .string(request.notice.sessionID)], onlyOn: connection)
            guard isCurrentContext(), host.connectionID == connection, host.isLive, host.readOnlyReason == nil,
                  let fresh = BridgeSession(json: raw), let latest = host.sessions.first(where: { $0.id == request.notice.sessionID }),
                  host.attempts[slot]?.isSending != true, host.attempts[slot]?.uncertain != true else { return notSent }
            _ = try BridgeNotificationActions.operation(request, current: latest, scope: scope, capabilities: host.capabilities, isLive: host.isLive)
            let operation = try BridgeNotificationActions.operation(request, current: fresh, scope: scope, capabilities: host.capabilities, isLive: host.isLive)
            await host.perform(operation, slot: slot)
            guard isCurrentContext(), let attempt = host.attempts[slot], attempt.operation.requestID == operation.requestID else { return notSent }
            guard let outcome = attempt.result?["outcome"]?.string else { return "Delivery was not confirmed. Check the session before trying again." }
            switch outcome {
            case "delivered": return "Delivered."
            case "unconfirmed": return "Delivery was not confirmed. Check the session at the machine."
            case "stale": return "Not sent. The question changed. Review the session."
            case "busy", "refused": return "Not sent. The machine did not accept this action. Review the session."
            default: return "Delivery was not confirmed. Check the session at the machine."
            }
        } catch { return notSent }
    }
}
