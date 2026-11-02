@testable import AgentBridgeAppleCore
import Foundation
import Testing

@Test func repliesRejectCommandsAndPendingQuestionsAndPreserveRequestIdentity() throws {
    let capabilities: Set = ["sessions.reply"]
    for text in [" \n/exit", "\u{0000}\t!rm", String(repeating: "🦋", count: 4097), "\u{0000}\n "] {
        #expect(throws: BridgeOperationError.self) { try BridgeSessionOperations.reply(sessionID: "s", text: text, hasQuestion: false, capabilities: capabilities) }
    }
    #expect(throws: BridgeOperationError.self) { try BridgeSessionOperations.reply(sessionID: "s", text: "yes", hasQuestion: true, capabilities: capabilities) }
    #expect(throws: BridgeOperationError.self) { try BridgeSessionOperations.reply(sessionID: "s", text: "yes", hasQuestion: false, capabilities: []) }
    let operation = try BridgeSessionOperations.reply(sessionID: "s", text: "Keep\ngoing", hasQuestion: false, capabilities: capabilities)
    #expect(operation.data["text"]?.string == "Keep\ngoing")
    #expect(operation.request(id: 1).data == operation.request(id: 2).data)
    #expect(operation.data["request"]?.string == operation.requestID?.uuidString.lowercased())
}

@Test func unknownDeliveryAndStopOutcomesNeverClaimSuccess() throws {
    let unknown = try JSONDecoder().decode(JSONValue.self, from: Data(#"{"outcome":"new-host-outcome","reason":"uncertain"}"#.utf8))
    let delivery = BridgeDeliveryResult(json: unknown)
    #expect(delivery.outcome == .unconfirmed)
    #expect(!delivery.allowsNewAttempt)
    #expect(BridgeStopResult(json: unknown, sessionID: "s").outcome == .failed)
    #expect(BridgeStopResult(json: .object(["outcome": .string("confirm")]), sessionID: "s").outcome == .failed)
}

@Test func endingAnActiveSessionRequiresAFreshSecondPressBoundToItsToken() throws {
    let now = Date(timeIntervalSince1970: 100)
    let first = try BridgeSessionOperations.stop(sessionID: "s", capabilities: ["sessions.stop"])
    #expect(first.data["confirm"] == nil)
    let fixture = try JSONDecoder().decode(JSONValue.self, from: Data(#"{"outcome":"confirm","confirm":"abc_DEF-123","confirmSeconds":10,"reason":"still working"}"#.utf8))
    let result = BridgeStopResult(json: fixture, sessionID: "s", receivedAt: now)
    let confirmation = try #require(result.confirmation)
    let second = try BridgeSessionOperations.stop(sessionID: "s", confirmation: confirmation, capabilities: ["sessions.stop"], now: now.addingTimeInterval(1))
    #expect(second.requestID != first.requestID)
    #expect(second.data["confirm"]?.string == "abc_DEF-123")
    #expect(throws: BridgeOperationError.self) { try BridgeSessionOperations.stop(sessionID: "other", confirmation: confirmation, capabilities: ["sessions.stop"], now: now) }
    #expect(throws: BridgeOperationError.self) { try BridgeSessionOperations.stop(sessionID: "s", confirmation: confirmation, capabilities: ["sessions.stop"], now: now.addingTimeInterval(10)) }
}
