import Foundation

/// A prepared mutation keeps its UUID and payload together. The channel assigns its own
/// envelope ID; a lost response must never turn into an automatic, newly identified press.
public struct BridgeOperation: Sendable, Equatable {
    public let op: String
    public let data: [String: JSONValue]
    public let requestID: UUID?

    public init(op: String, data: [String: JSONValue] = [:], requestID: UUID? = nil) {
        self.op = op
        self.data = data
        self.requestID = requestID
    }

    public func request(id: Int) -> BridgeRequest {
        BridgeRequest(id: id, op: op, data: data)
    }
}

public struct BridgeOperationError: LocalizedError, Sendable, Equatable {
    public let message: String
    public var errorDescription: String? {
        message
    }

    public init(_ message: String) {
        self.message = message
    }
}

public struct BridgeDeliveryResult: Sendable, Equatable {
    public enum Outcome: String, Sendable {
        case delivered, unconfirmed, stale, busy, refused
    }

    public let outcome: Outcome
    public let reason: String?
    public var allowsNewAttempt: Bool {
        outcome == .busy || outcome == .refused
    }

    public init(json: JSONValue) {
        outcome = Outcome(rawValue: json["outcome"]?.string ?? "") ?? .unconfirmed
        reason = json["reason"]?.string
    }
}

public enum BridgeSessionOperations {
    public static func reply(sessionID: String, text: String, attachments: [UUID] = [],
                             hasQuestion: Bool, capabilities: Set<String>, requestID: UUID = UUID()) throws -> BridgeOperation
    {
        try BridgeOperationValidation.require("sessions.reply", in: capabilities)
        guard !sessionID.isEmpty else { throw BridgeOperationError("The session is missing.") }
        guard !hasQuestion else { throw BridgeOperationError("Answer the pending question before sending a reply.") }
        try BridgeOperationValidation.attachments(attachments, capabilities: capabilities)
        try BridgeOperationValidation.prompt(text, allowEmpty: !attachments.isEmpty)
        var data: [String: JSONValue] = ["id": .string(sessionID), "request": .string(requestID.uuidString.lowercased()), "text": .string(text)]
        if !attachments.isEmpty {
            data["attachments"] = BridgeOperationValidation.attachmentReferences(attachments)
        }
        // Reserve the longest channel ID before HostStore records a mutation. A local
        // encoding failure must not become an uncertain, possibly delivered reply.
        try BridgeOperationValidation.requestSize(op: "session.reply", data: data)
        return BridgeOperation(op: "session.reply", data: data, requestID: requestID)
    }

    public static func stop(sessionID: String, confirmation: BridgeStopConfirmation? = nil,
                            capabilities: Set<String>, requestID: UUID = UUID(), now: Date = Date()) throws -> BridgeOperation
    {
        try BridgeOperationValidation.require("sessions.stop", in: capabilities)
        guard !sessionID.isEmpty else { throw BridgeOperationError("The session is missing.") }
        var data: [String: JSONValue] = ["id": .string(sessionID), "request": .string(requestID.uuidString.lowercased())]
        if let confirmation {
            guard confirmation.sessionID == sessionID, confirmation.expiresAt > now else {
                throw BridgeOperationError("The confirmation expired or belongs to another session. Press End session again.")
            }
            data["confirm"] = .string(confirmation.token)
        }
        return BridgeOperation(op: "session.stop", data: data, requestID: requestID)
    }
}

public struct BridgeStopConfirmation: Sendable, Equatable {
    public let sessionID: String
    public let token: String
    public let expiresAt: Date
}

public struct BridgeStopResult: Sendable, Equatable {
    public enum Outcome: String, Sendable { case ended, confirm, stale, busy, refused, failed }
    public let outcome: Outcome
    public let reason: String?
    public let forced: Bool
    public let confirmation: BridgeStopConfirmation?

    public init(json: JSONValue, sessionID: String, receivedAt: Date = Date()) {
        let parsed = Outcome(rawValue: json["outcome"]?.string ?? "") ?? .failed
        reason = json["reason"]?.string
        forced = json["forced"]?.bool == true
        if parsed == .confirm, let token = json["confirm"]?.string,
           !token.isEmpty, token.utf8.count <= 64,
           token.utf8.allSatisfy({ (65 ... 90).contains($0) || (97 ... 122).contains($0) || (48 ... 57).contains($0) || $0 == 45 || $0 == 95 }),
           let seconds = json["confirmSeconds"]?.number, seconds.isFinite, seconds > 0
        {
            confirmation = BridgeStopConfirmation(sessionID: sessionID, token: token, expiresAt: receivedAt.addingTimeInterval(seconds))
            outcome = .confirm
        } else {
            confirmation = nil
            outcome = parsed == .confirm ? .failed : parsed
        }
    }
}

enum BridgeOperationValidation {
    static func require(_ capability: String, in capabilities: Set<String>) throws {
        guard capabilities.contains(capability) else { throw BridgeOperationError("This machine does not offer \(capability).") }
    }

    static func blank(_ text: String) -> Bool {
        text.unicodeScalars.allSatisfy { CharacterSet.whitespacesAndNewlines.contains($0) || CharacterSet.controlCharacters.contains($0) }
    }

    static func prompt(_ text: String, allowEmpty: Bool) throws {
        guard allowEmpty || !blank(text) else { throw BridgeOperationError("Enter a message or attach a file.") }
        guard text.utf8.count <= 16 * 1024 else { throw BridgeOperationError("The message is longer than 16 KiB.") }
        let first = text.unicodeScalars.first { !CharacterSet.whitespacesAndNewlines.contains($0) && !CharacterSet.controlCharacters.contains($0) }
        guard first != "/", first != "!" else { throw BridgeOperationError("Messages cannot begin with / or !. Type commands at the machine.") }
    }

    static func attachments(_ ids: [UUID], capabilities: Set<String>) throws {
        guard ids.count <= 10 else { throw BridgeOperationError("A message can carry at most 10 attachments.") }
        guard Set(ids).count == ids.count else { throw BridgeOperationError("The same attachment appears more than once.") }
        if !ids.isEmpty {
            try require("sessions.attach", in: capabilities)
        }
    }

    static func attachmentReferences(_ ids: [UUID]) -> JSONValue {
        .array(ids.map { .object(["id": .string($0.uuidString.lowercased())]) })
    }

    static func encodedSize(_ data: [String: JSONValue], maximum: Int) throws {
        guard try JSONEncoder().encode(JSONValue.object(data)).count <= maximum else {
            throw BridgeOperationError("The request is larger than \(maximum / 1024) KiB.")
        }
    }

    static func requestSize(op: String, data: [String: JSONValue]) throws {
        let request = BridgeRequest(id: Int.max, op: op, data: data)
        guard try JSONEncoder().encode(request).count <= 48 * 1024 else {
            throw BridgeOperationError("The encoded message is larger than 48 KiB. Shorten it before sending.")
        }
    }
}
