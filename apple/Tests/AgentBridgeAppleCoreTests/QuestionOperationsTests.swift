@testable import AgentBridgeAppleCore
import Foundation
import Testing

private func questionFixture(_ source: String) throws -> BridgeQuestion {
    try #require(BridgeQuestion(json: JSONDecoder().decode(JSONValue.self, from: Data(source.utf8))))
}

@Test func questionAnswersKeepAllFieldTypesAndTheDisplayedIdentity() throws {
    let question = try questionFixture(#"{"id":"question-1","kind":"form","message":"Configure it","fields":[{"name":"choice","type":"choice","required":true,"allowOther":true,"options":[{"value":"a","label":"A"},{"value":"b","label":"B (Recommended)"}]},{"name":"multi","type":"multi","allowOther":true,"options":[{"value":"x"}]},{"name":"yes","type":"boolean"},{"name":"notes","type":"text"},{"name":"count","type":"number"}]}"#)
    #expect(question.fields[0].displayOptions.map(\.value) == ["b", "a"])
    let values: [String: JSONValue] = ["choice": .object(["other": .string("different")]), "multi": .array([.string("x"), .object(["other": .string("also")])]), "yes": .bool(false), "notes": .string("two\nlines"), "count": .number(3.5)]
    let press = UUID()
    let operation = try question.answer(sessionID: "session-1", currentQuestionID: "question-1", values: values, capabilities: ["sessions.answer"], sessionAgent: "copilot", requestID: press)
    let encoded = try JSONDecoder().decode(JSONValue.self, from: JSONEncoder().encode(operation.request(id: 7)))
    #expect(encoded["op"]?.string == "session.answer")
    #expect(encoded["id"]?.number == 7)
    #expect(encoded["data"]?["question"]?.string == "question-1")
    #expect(encoded["data"]?["request"]?.string == press.uuidString.lowercased())
    #expect(encoded["data"]?["fields"] == .object(values))
    #expect(throws: BridgeOperationError.self) {
        try question.answer(sessionID: "session-1", currentQuestionID: "new-question", values: values, capabilities: ["sessions.answer"], sessionAgent: "copilot")
    }
}

@Test func requiredChoicesAndOtherAnswersAreValidatedBeforeSending() throws {
    let question = try questionFixture(#"{"id":"q","fields":[{"name":"choice","title":"Choose","type":"multi","required":true,"allowOther":true,"options":[{"value":"a"}]}]}"#)
    let invalid: [[String: JSONValue]] = [[:], ["extra": .string("a")], ["choice": .null], ["choice": .array([.string("unknown")])], ["choice": .array([.object(["other": .string(" ")])])], ["choice": .array([.object(["other": .string("a")]), .object(["other": .string("b")])])], ["choice": .array([.object(["other": .string("a"), "extra": .bool(true)])])]]
    for values in invalid {
        #expect(throws: BridgeOperationError.self) {
            try question.answer(sessionID: "s", currentQuestionID: "q", values: values, capabilities: ["sessions.answer"], sessionAgent: "copilot")
        }
    }
    #expect(throws: BridgeOperationError.self) {
        try question.answer(sessionID: "s", currentQuestionID: "q", values: ["choice": .array([])], capabilities: ["sessions.answer"], sessionAgent: "claude")
    }
}

@Test func answerLimitsCountUTF8AndTheWholeEncodedPayload() throws {
    let plain = try questionFixture(#"{"id":"q","fields":[]}"#)
    #expect(throws: BridgeOperationError.self) {
        try plain.answer(sessionID: "s", currentQuestionID: "q", text: String(repeating: "🦋", count: 1025), capabilities: ["sessions.answer"], sessionAgent: "copilot")
    }
    let form = try questionFixture(#"{"id":"q","fields":[{"name":"a","type":"text"},{"name":"b","type":"text"},{"name":"c","type":"text"},{"name":"d","type":"text"}]}"#)
    let values = Dictionary(uniqueKeysWithValues: ["a", "b", "c", "d"].map { ($0, JSONValue.string(String(repeating: "x", count: 4096))) })
    #expect(throws: BridgeOperationError.self) {
        try form.answer(sessionID: "s", currentQuestionID: "q", values: values, capabilities: ["sessions.answer"], sessionAgent: "copilot")
    }
}

@Test func approvalCannotBroadenPermissionOrBypassReadOnlyFlags() throws {
    let question = try questionFixture(#"{"id":"permission#digest","kind":"approval","message":"Run command?","fields":[{"name":"decision","type":"choice","options":[{"value":"approve"},{"value":"deny"},{"value":"always"}]}]}"#)
    #expect(question.readOnlyReason(capabilities: ["sessions.answer"], sessionAgent: "copilot") != nil)
    let capabilities: Set = ["sessions.answer", "sessions.approve"]
    let allowed = try question.answer(sessionID: "s", currentQuestionID: question.id, values: ["decision": .string("approve")], capabilities: capabilities, sessionAgent: "copilot")
    #expect(allowed.data["fields"]?["decision"]?.string == "approve")
    #expect(throws: BridgeOperationError.self) {
        try question.answer(sessionID: "s", currentQuestionID: question.id, values: ["decision": .string("always")], capabilities: capabilities, sessionAgent: "copilot")
    }
    for flag in ["truncated", "answerAtMachine"] {
        let readOnly = try questionFixture("{\"id\":\"q\",\"\(flag)\":true,\"fields\":[]}")
        #expect(throws: BridgeOperationError.self) {
            try readOnly.answer(sessionID: "s", currentQuestionID: "q", text: "yes", capabilities: capabilities, sessionAgent: "copilot")
        }
    }
}
