import Foundation

public struct BridgeQuestionOption: Identifiable, Sendable, Equatable {
    public var id: String {
        value
    }

    public let value: String
    public let label: String
    public var recommended: Bool {
        label.localizedCaseInsensitiveContains("(recommended)")
    }

    public init?(json: JSONValue) {
        guard let value = json["value"]?.string else { return nil }
        self.value = value
        label = json["label"]?.string ?? value
    }
}

public struct BridgeQuestionField: Identifiable, Sendable, Equatable {
    public enum Kind: String, Sendable { case choice, multi, boolean, text, number }
    public var id: String {
        name
    }

    public let name: String
    public let title: String
    public let description: String?
    public let kind: Kind
    public let options: [BridgeQuestionOption]
    public let allowOther: Bool
    public let required: Bool
    public let defaultValue: JSONValue?

    public init?(json: JSONValue) {
        guard let name = json["name"]?.string, !name.isEmpty else { return nil }
        self.name = name
        title = json["title"]?.string ?? name
        description = json["description"]?.string
        kind = Kind(rawValue: json["type"]?.string ?? "") ?? .text
        options = (json["options"]?.array ?? []).compactMap(BridgeQuestionOption.init)
        allowOther = json["allowOther"]?.bool == true
        required = json["required"]?.bool == true
        defaultValue = json["default"]
    }

    public var displayOptions: [BridgeQuestionOption] {
        options.filter(\.recommended) + options.filter { !$0.recommended }
    }

    func validate(_ value: JSONValue) throws {
        switch kind {
        case .choice:
            try validateChoice(value)
        case .multi:
            guard let values = value.array else { throw problem("Choose one or more options.") }
            var others = 0
            for item in values {
                if case .object = item {
                    others += 1
                }
                try validateChoice(item)
            }
            guard others <= 1 else { throw problem("Use at most one Other answer.") }
        case .boolean:
            guard value.bool != nil else { throw problem("Choose true or false.") }
        case .number:
            guard let number = value.number, number.isFinite else { throw problem("Enter a finite number.") }
        case .text:
            guard let text = value.string else { throw problem("Enter text.") }
            try validateText(text)
        }
    }

    private func validateChoice(_ value: JSONValue) throws {
        if let choice = value.string, options.contains(where: { $0.value == choice }) {
            return
        }
        if case let .object(other) = value, other.count == 1, allowOther,
           let text = other["other"]?.string, !BridgeOperationValidation.blank(text)
        {
            try validateText(text)
            return
        }
        throw problem("Choose an offered option or provide an allowed Other answer.")
    }

    private func validateText(_ text: String) throws {
        guard text.utf8.count <= 4096 else { throw problem("The text is longer than 4 KiB.") }
    }

    private func problem(_ text: StaticString) -> BridgeOperationError {
        BridgeOperationError(text)
    }
}

public struct BridgeQuestion: Identifiable, Sendable, Equatable {
    public let id: String
    public let isApproval: Bool
    public let message: String
    public let fields: [BridgeQuestionField]
    public let truncated: Bool
    public let answerAtMachine: Bool
    public let agent: String?

    public init?(json: JSONValue) {
        guard let id = json["id"]?.string, !id.isEmpty else { return nil }
        let rawFields = json["fields"]?.array ?? []
        let fields = rawFields.compactMap(BridgeQuestionField.init)
        guard rawFields.count == fields.count, Set(fields.map(\.name)).count == fields.count else { return nil }
        self.id = id
        isApproval = json["kind"]?.string == "approval"
        message = json["message"]?.string ?? ""
        self.fields = fields
        truncated = json["truncated"]?.bool == true
        answerAtMachine = json["answerAtMachine"]?.bool == true
        agent = json["agent"]?.string
    }

    public func readOnlyReason(capabilities: Set<String>, sessionAgent: String) -> String? {
        if truncated {
            return "This question could not be shown in full. Answer at the machine."
        }
        if isApproval, sessionAgent == "copilot", !capabilities.contains("sessions.approve") {
            return "Remote approval is turned off on this machine. Answer at the machine."
        }
        // This app does not yet check a Codex approval's shape as the browser and Windows apps do
        // (#209): an older host sends a cut preview of the command, unmarked, which Approve once
        // would run whole. Until it does, every Codex approval is answered at the machine.
        if isApproval, sessionAgent == "codex" {
            return "Codex approvals can only be answered at the machine from this app for now."
        }
        if answerAtMachine {
            return "This prompt can only be answered at the machine."
        }
        if !capabilities.contains("sessions.answer") {
            return "This machine is not taking answers. Answer at the machine."
        }
        return nil
    }

    public func answer(sessionID: String, currentQuestionID: String?, values: [String: JSONValue] = [:], text: String? = nil,
                       capabilities: Set<String>, sessionAgent: String, requestID: UUID = UUID()) throws -> BridgeOperation
    {
        if readOnlyReason(capabilities: capabilities, sessionAgent: sessionAgent) != nil {
            throw BridgeOperationError("This question cannot be answered from this app. Answer at the machine.")
        }
        guard !sessionID.isEmpty, currentQuestionID == id else { throw BridgeOperationError("This question has changed or is no longer pending.") }
        var data: [String: JSONValue] = ["id": .string(sessionID), "question": .string(id), "request": .string(requestID.uuidString.lowercased())]
        if fields.isEmpty {
            guard values.isEmpty, let text, !BridgeOperationValidation.blank(text) else { throw BridgeOperationError("Enter an answer.") }
            guard text.utf8.count <= 4096 else { throw BridgeOperationError("The answer is longer than 4 KiB.") }
            data["text"] = .string(text)
        } else {
            guard text == nil else { throw BridgeOperationError("This question takes fields, not a text answer.") }
            guard !values.isEmpty, Set(values.keys).isSubset(of: Set(fields.map(\.name))) else { throw BridgeOperationError("Answer the fields shown in this question.") }
            for field in fields {
                guard let value = values[field.name], value != .null else {
                    if field.required {
                        throw BridgeOperationError("A required field needs an answer.")
                    }
                    continue
                }
                try field.validate(value)
                if sessionAgent == "claude", field.kind == .multi, value.array?.isEmpty == true {
                    throw BridgeOperationError("A field needs at least one choice.")
                }
            }
            data["fields"] = .object(values)
        }
        if isApproval {
            guard values.count == 1, let decision = values["decision"]?.string, ["approve", "deny"].contains(decision) else {
                throw BridgeOperationError("A permission prompt accepts only Approve once or Deny.")
            }
        }
        try BridgeAnswerJSONSize.validate(data)
        return BridgeOperation(op: "session.answer", data: data, requestID: requestID)
    }
}
