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

@Test func replyRejectsEscapingExpansionBeforeAMutationCanBePrepared() throws {
    let text = "Continue" + String(repeating: "\u{0000}", count: 12000)
    #expect(text.utf8.count < 16 * 1024)
    #expect(throws: BridgeOperationError.self) {
        try BridgeSessionOperations.reply(sessionID: "s", text: text, hasQuestion: false, capabilities: ["sessions.reply"])
    }
    // A rejected draft does not prevent the next ordinary press being prepared.
    let next = try BridgeSessionOperations.reply(sessionID: "s", text: "Continue", hasQuestion: false, capabilities: ["sessions.reply"])
    #expect(next.data["text"]?.string == "Continue")
}

@Test func replyBudgetIncludesTheEnvelopeLongestChannelIDAndAllAttachments() throws {
    let requestID = try #require(UUID(uuidString: "00000000-0000-0000-0000-000000000001"))
    let attachments = (1 ... 10).map { UUID(uuidString: String(format: "00000000-0000-0000-0000-%012d", $0))! }
    let capabilities: Set = ["sessions.reply", "sessions.attach"]
    let base = try BridgeSessionOperations.reply(sessionID: "s", text: "Continue", attachments: attachments,
                                                 hasQuestion: false, capabilities: capabilities, requestID: requestID)
    let overhead = try JSONEncoder().encode(base.request(id: Int.max)).count
    let available = 48 * 1024 - overhead
    let text = "Continue" + String(repeating: "\u{0000}", count: available / 6) + String(repeating: "x", count: available % 6)
    let boundary = try BridgeSessionOperations.reply(sessionID: "s", text: text, attachments: attachments,
                                                     hasQuestion: false, capabilities: capabilities, requestID: requestID)
    #expect(try JSONEncoder().encode(boundary.request(id: Int.max)).count == 48 * 1024)
    // Data alone still fits: only the complete request crosses the budget.
    var tooLarge = boundary.data
    tooLarge["text"] = .string(text + "x")
    #expect(try JSONEncoder().encode(JSONValue.object(tooLarge)).count < 48 * 1024)
    #expect(throws: BridgeOperationError.self) {
        try BridgeSessionOperations.reply(sessionID: "s", text: text + "x", attachments: attachments,
                                          hasQuestion: false, capabilities: capabilities, requestID: requestID)
    }
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
