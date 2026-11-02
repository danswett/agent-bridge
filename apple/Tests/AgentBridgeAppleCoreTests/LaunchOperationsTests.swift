@testable import AgentBridgeAppleCore
import Foundation
import Testing

private func launchOptionsFixture() throws -> BridgeLaunchOptions {
    let fixture = #"{"agents":[{"id":"agency","label":"Agency","kind":"copilot","models":["model-1"],"efforts":["high"],"profiles":["work"],"attachments":true},{"id":"codex","label":"Codex","kind":"codex","needsPrompt":true}],"workspaces":[{"label":"Bridge","path":"/workspace/bridge","isolate":true}],"resumable":[{"id":"old-session","agent":"codex","launcher":"codex","title":"Resume work"}],"defaults":{"agent":"agency","workspace":"Bridge","permissions":"ask"}}"#
    return try BridgeLaunchOptions(json: JSONDecoder().decode(JSONValue.self, from: Data(fixture.utf8)))
}

@Test func launchUsesOfferedValuesAndRetainsThePressUUID() throws {
    let options = try launchOptionsFixture()
    var draft = BridgeLaunchDraft()
    draft.agent = "agency"
    draft.workspace = "Bridge"
    draft.profile = "work"
    draft.model = "model-1"
    draft.effort = ""
    draft.prompt = "Read the docs\nthen continue."
    let press = UUID()
    let operation = try draft.operation(options: options, capabilities: ["sessions.launch"], requestID: press)
    #expect(operation.op == "session.launch")
    #expect(operation.data["request"]?.string == press.uuidString.lowercased())
    #expect(operation.data["effort"] == .string(""))
    #expect(operation.data["prompt"] == .string(draft.prompt))
    #expect(operation.data["permissions"] == nil)
    draft.model = "not-offered"
    #expect(throws: BridgeOperationError.self) { try draft.operation(options: options, capabilities: ["sessions.launch"]) }
    draft.model = "--unsafe"
    #expect(throws: BridgeOperationError.self) { try draft.operation(options: options, capabilities: ["sessions.launch"]) }
}

@Test func codexNeedsAnOpeningPromptButAResumeDoesNot() throws {
    let options = try launchOptionsFixture()
    var draft = BridgeLaunchDraft()
    draft.agent = "codex"
    #expect(throws: BridgeOperationError.self) { try draft.operation(options: options, capabilities: ["sessions.launch"]) }
    draft.prompt = "Implement the change"
    #expect(try draft.operation(options: options, capabilities: ["sessions.launch"]).data["agent"] == .string("codex"))
    draft.resume = "old-session"
    draft.prompt = ""
    #expect(try draft.operation(options: options, capabilities: ["sessions.launch"]).data["resume"] == .string("old-session"))
    draft.workspace = "Bridge"
    #expect(throws: BridgeOperationError.self) { try draft.operation(options: options, capabilities: ["sessions.launch"]) }
}

@Test func launchRefusesCommandsAndUnknownOutcomesRemainUnconfirmed() throws {
    let options = try launchOptionsFixture()
    var draft = BridgeLaunchDraft()
    draft.prompt = "\n\u{0000}/exit"
    #expect(throws: BridgeOperationError.self) { try draft.operation(options: options, capabilities: ["sessions.launch"]) }
    draft.prompt = "continue"
    #expect(throws: BridgeOperationError.self) { try draft.operation(options: options, capabilities: []) }
    let result = BridgeLaunchResult(json: .object(["outcome": .string("future"), "session": .string("s")]))
    #expect(result.outcome == .unconfirmed)
    #expect(result.sessionID == "s")
    #expect(!result.allowsNewAttempt)
}
