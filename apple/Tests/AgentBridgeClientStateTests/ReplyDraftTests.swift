import AgentBridgeAppleCore
@testable import AgentBridgeClientState
import Foundation
import Testing

private actor ReplyDraftHost {
    enum Delivery { case immediate, held, loseFirst }
    let delivery: Delivery
    let messages = AsyncThrowingStream<BridgeEnvelope, any Error>.makeStream()
    private(set) var replyRequests: [BridgeRequest] = []
    private var pending: CheckedContinuation<JSONValue, Never>?
    private var sequence = 0

    init(_ delivery: Delivery = .immediate) {
        self.delivery = delivery
    }

    func transport() -> HostTransport {
        HostTransport(messages: messages.stream, send: { request, _ in try await self.send(request) }, close: { await self.close() }, peerKey: nil)
    }

    func close() {
        messages.continuation.finish()
    }

    var isPending: Bool {
        pending != nil
    }

    func finish(_ outcome: String = "delivered") {
        pending?.resume(returning: .object(["outcome": .string(outcome)]))
        pending = nil
    }

    func askQuestion() throws {
        try event("session.upsert", data: .object([
            "id": .string("s1"), "agent": .string("codex"), "status": .string("waiting"),
            "question": .object(["id": .string("q1"), "kind": .string("form"), "message": .string("Continue?")]),
        ]))
    }

    private func send(_ request: BridgeRequest) async throws -> BridgeEnvelope {
        let result: JSONValue
        switch request.op {
        case "hello":
            result = .object([
                "protocol": .number(1),
                "capabilities": .array(["sessions.read", "sessions.reply", "sessions.answer"].map(JSONValue.string)),
                "machine": .object(["id": .string("fixture"), "name": .string("Fixture"), "instance": .string("run-1")]),
            ])
        case "session.reply":
            replyRequests.append(request)
            if delivery == .loseFirst, replyRequests.count == 1 {
                throw RelayError.timeout
            }
            if delivery == .held {
                result = await withCheckedContinuation { pending = $0 }
            } else {
                result = .object(["outcome": .string("delivered")])
            }
        case "session.answer": result = .object(["outcome": .string("delivered")])
        default: result = .object([:])
        }
        let response = try envelope(["re": .number(Double(request.id)), "ok": .bool(true), "result": result])
        messages.continuation.yield(response)
        if request.op == "sessions.subscribe" {
            try event("session.upsert", data: .object(["id": .string("s1"), "agent": .string("codex"), "status": .string("idle")]))
            try event("sessions.synced", data: .object([:]))
        }
        return response
    }

    private func event(_ name: String, data: JSONValue) throws {
        sequence += 1
        try messages.continuation.yield(envelope(["ev": .string(name), "seq": .number(Double(sequence)), "data": data]))
    }

    private func envelope(_ fields: [String: JSONValue]) throws -> BridgeEnvelope {
        try JSONDecoder().decode(BridgeEnvelope.self, from: JSONEncoder().encode(JSONValue.object(fields)))
    }
}

@MainActor private func awaitDraftState(_ condition: @MainActor () async -> Bool) async throws {
    for _ in 0 ..< 400 {
        if await condition() {
            return
        }
        try await Task.sleep(for: .milliseconds(5))
    }
    throw RelayError.timeout
}

@MainActor private func connectedDraftHost(_ fixture: ReplyDraftHost) async throws -> HostStore {
    let host = HostStore(id: "fixture", name: "Fixture", isLocal: false) { await fixture.transport() }
    host.start()
    try await awaitDraftState { host.isLive && host.sessions.count == 1 }
    return host
}

@MainActor @Test func deliveredReplyRemovesOnlyItsPrefixWithoutAnOpenEditor() async throws {
    let fixture = ReplyDraftHost(.held)
    let host = try await connectedDraftHost(fixture)
    defer { host.stop() }
    host.replyDrafts["s1"] = "Thanks.\n"
    let operation = try BridgeSessionOperations.reply(sessionID: "s1", text: "Thanks.\n", hasQuestion: false, capabilities: host.capabilities)
    let sending = Task { await host.perform(operation, slot: "session:s1", consumesReplyDraft: true) }
    try await awaitDraftState { await fixture.isPending }
    host.replyDrafts["s1", default: ""] += "Please stop your delegate witness."
    await fixture.finish()
    await sending.value
    #expect(host.replyDrafts["s1"] == "Please stop your delegate witness.")
    #expect(await fixture.replyRequests.count == 1)
}

@MainActor @Test func repeatedReceiptCannotEraseANewIdenticalDraft() async throws {
    let fixture = ReplyDraftHost()
    let host = try await connectedDraftHost(fixture)
    defer { host.stop() }
    let operation = try BridgeSessionOperations.reply(sessionID: "s1", text: "Continue", hasQuestion: false, capabilities: host.capabilities)
    host.replyDrafts["s1"] = "Continue"
    await host.perform(operation, slot: "session:s1", consumesReplyDraft: true)
    #expect(host.replyDrafts["s1"] == "")
    host.replyDrafts["s1"] = "Continue"
    // A duplicate request gets the same receipt, as a host's request cache does.
    await host.perform(operation, slot: "session:s1", consumesReplyDraft: true)
    #expect(host.replyDrafts["s1"] == "Continue")
    #expect(await fixture.replyRequests.map { $0.data["request"] } == [operation.data["request"], operation.data["request"]])
}

@MainActor @Test func retryCleansTheDeliveredPrefixAndKeepsTheAppendedDraft() async throws {
    let fixture = ReplyDraftHost(.loseFirst)
    let host = try await connectedDraftHost(fixture)
    defer { host.stop() }
    host.replyDrafts["s1"] = "Continue"
    let operation = try BridgeSessionOperations.reply(sessionID: "s1", text: "Continue", hasQuestion: false, capabilities: host.capabilities)
    await host.perform(operation, slot: "session:s1", consumesReplyDraft: true)
    #expect(host.attempts["session:s1"]?.uncertain == true)
    #expect(host.replyDrafts["s1"] == "Continue")
    host.replyDrafts["s1", default: ""] += " Please stop your delegate witness."
    await host.retry("session:s1")
    #expect(host.replyDrafts["s1"] == "Please stop your delegate witness.")
    #expect(await fixture.replyRequests.map { $0.data["request"] } == [operation.data["request"], operation.data["request"]])
}

@MainActor @Test func retryOfANonComposerReplyPreservesTheMatchingSavedDraft() async throws {
    let fixture = ReplyDraftHost(.loseFirst)
    let host = try await connectedDraftHost(fixture)
    defer { host.stop() }
    host.replyDrafts["s1"] = "Continue with tests"
    let operation = try BridgeSessionOperations.reply(sessionID: "s1", text: "Continue", hasQuestion: false, capabilities: host.capabilities)
    await host.perform(operation, slot: "session:s1")
    #expect(host.attempts["session:s1"]?.uncertain == true)
    await host.retry("session:s1")
    #expect(host.attempts["session:s1"]?.result?["outcome"] == .string("delivered"))
    #expect(host.replyDrafts["s1"] == "Continue with tests")
    #expect(await fixture.replyRequests.map { $0.data["request"] } == [operation.data["request"], operation.data["request"]])
}

@MainActor @Test func newQuestionAndItsAnswerDoNotReplaceThePendingReplyDraft() async throws {
    let fixture = ReplyDraftHost(.held)
    let host = try await connectedDraftHost(fixture)
    defer { host.stop() }
    host.replyDrafts["s1"] = "Continue"
    let operation = try BridgeSessionOperations.reply(sessionID: "s1", text: "Continue", hasQuestion: false, capabilities: host.capabilities)
    let sending = Task { await host.perform(operation, slot: "session:s1", consumesReplyDraft: true) }
    try await awaitDraftState { await fixture.isPending }
    try await fixture.askQuestion()
    try await awaitDraftState { host.sessions.first?.question?["id"]?.string == "q1" }
    host.replyDrafts["s1", default: ""] += " Please stop your delegate witness."
    await fixture.finish()
    await sending.value
    #expect(host.replyDrafts["s1"] == "Please stop your delegate witness.")
    let request = UUID()
    let answer = BridgeOperation(op: "session.answer", data: ["id": .string("s1"), "question": .string("q1"),
                                                              "request": .string(request.uuidString.lowercased()), "text": .string("Please stop your delegate witness.")], requestID: request)
    await host.perform(answer, slot: "session:s1")
    #expect(host.replyDrafts["s1"] == "Please stop your delegate witness.")
    #expect(host.sessions.first?.question?["id"]?.string == "q1")
}

@MainActor @Test(arguments: ["refused", "busy", "stale", "unconfirmed", "future-outcome"])
func undeliveredOrUncertainReceiptsKeepTheWholeDraft(outcome: String) async throws {
    let fixture = ReplyDraftHost(.held)
    let host = try await connectedDraftHost(fixture)
    defer { host.stop() }
    host.replyDrafts["s1"] = "Continue and keep this draft."
    let operation = try BridgeSessionOperations.reply(sessionID: "s1", text: "Continue", hasQuestion: false, capabilities: host.capabilities)
    let sending = Task { await host.perform(operation, slot: "session:s1", consumesReplyDraft: true) }
    try await awaitDraftState { await fixture.isPending }
    await fixture.finish(outcome)
    await sending.value
    #expect(host.replyDrafts["s1"] == "Continue and keep this draft.")
}

@MainActor @Test(arguments: ["A replacement draft", "Cafe\u{301}"])
func deliveredReplyLeavesAChangedOrMerelyCanonicallyEquivalentDraft(draft: String) async throws {
    let fixture = ReplyDraftHost()
    let host = try await connectedDraftHost(fixture)
    defer { host.stop() }
    host.replyDrafts["s1"] = draft
    let operation = try BridgeSessionOperations.reply(sessionID: "s1", text: "Café", hasQuestion: false, capabilities: host.capabilities)
    await host.perform(operation, slot: "session:s1", consumesReplyDraft: true)
    #expect(host.replyDrafts["s1"]?.utf8.elementsEqual(draft.utf8) == true)
}
