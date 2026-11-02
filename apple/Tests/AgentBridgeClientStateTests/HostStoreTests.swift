import AgentBridgeAppleCore
@testable import AgentBridgeClientState
import Foundation
import Testing

private actor FixtureHost {
    let stream = AsyncThrowingStream<BridgeEnvelope, any Error>.makeStream()
    let instance: String
    var loseReply: Bool
    var mutationIDs: [String] = []
    init(instance: String = "run-1", loseReply: Bool = false) {
        self.instance = instance; self.loseReply = loseReply
    }

    func connection() -> HostTransport {
        HostTransport(messages: stream.stream, send: { request, _ in try await self.send(request) },
                      close: { await self.close() }, peerKey: nil)
    }

    func close() {
        stream.continuation.finish(throwing: RelayError.disconnected)
    }

    func send(_ request: BridgeRequest) throws -> BridgeEnvelope {
        let result: JSONValue
        switch request.op {
        case "hello": result = .object(["protocol": .number(1), "capabilities": .array([.string("sessions.read"), .string("sessions.reply")]),
                                        "machine": .object(["id": .string("fixture"), "name": .string("Fixture"), "instance": .string(instance)])])
        case "sessions.subscribe": result = .object([:])
        case "session.reply":
            mutationIDs.append(request.data["request"]?.string ?? "missing")
            if loseReply {
                close(); throw RelayError.disconnected
            }
            result = .object(["outcome": .string("delivered")])
        default: result = .object([:])
        }
        let response = try envelope(.object(["re": .number(Double(request.id)), "ok": .bool(true), "result": result]))
        stream.continuation.yield(response)
        if request.op == "sessions.subscribe" {
            try stream.continuation.yield(envelope(.object(["ev": .string("session.upsert"), "seq": .number(1),
                                                            "data": .object(["id": .string("s1"), "title": .string("Test session"), "agent": .string("codex"), "status": .string("idle")])])))
            try stream.continuation.yield(envelope(.object(["ev": .string("sessions.synced"), "seq": .number(2), "data": .object([:])])))
        }
        return response
    }

    private func envelope(_ json: JSONValue) throws -> BridgeEnvelope {
        try JSONDecoder().decode(BridgeEnvelope.self, from: JSONEncoder().encode(json))
    }
}

@MainActor private func until(_ condition: () -> Bool) async throws {
    for _ in 0 ..< 200 {
        if condition() {
            return
        }
        try await Task.sleep(for: .milliseconds(5))
    }
    Issue.record("State did not settle")
}

@MainActor @Test func uncertainReplyKeepsItsIdentityWhenTheSameHostReconnects() async throws {
    let first = FixtureHost(loseReply: true)
    let host = HostStore(id: "host", name: "Fixture", isLocal: false) { await first.connection() }
    host.start()
    try await until { host.isLive }
    #expect(host.sessions.map(\.id) == ["s1"])
    let operation = try BridgeSessionOperations.reply(sessionID: "s1", text: "Continue", hasQuestion: false, capabilities: host.capabilities)
    await host.perform(operation, slot: "s1")
    #expect(host.attempts["s1"]?.uncertain == true)
    let second = FixtureHost()
    host.reconnect { await second.connection() }
    try await until { host.isLive }
    #expect(host.attempts["s1"]?.operation == operation)
    await host.retry("s1")
    #expect(host.attempts["s1"]?.result?["outcome"]?.string == "delivered")
    #expect(await first.mutationIDs == second.mutationIDs)
    host.stop()
}

@MainActor @Test func anUncertainReplyIsNeverSentToAReplacementHostProcess() async throws {
    let first = FixtureHost(loseReply: true)
    let host = HostStore(id: "host", name: "Fixture", isLocal: false) { await first.connection() }
    host.start()
    try await until { host.isLive }
    let operation = try BridgeSessionOperations.reply(sessionID: "s1", text: "Continue", hasQuestion: false, capabilities: host.capabilities)
    await host.perform(operation, slot: "s1")
    let replacement = FixtureHost(instance: "run-2")
    host.reconnect { await replacement.connection() }
    try await until { host.isLive }
    await host.retry("s1")
    #expect(await replacement.mutationIDs.isEmpty)
    #expect(host.attempts["s1"]?.uncertain == true)
    host.stop()
}

@Test func unknownMutationOutcomesRemainUncertainAndUpdateAcceptanceIsRecognized() {
    var attempt = MutationAttempt(operation: .init(op: "update.install"), hostInstance: "fixture", consumesReplyDraft: false)
    attempt.isSending = false
    attempt.result = .object(["outcome": .string("future-value")])
    #expect(attempt.uncertain)
    attempt.result = .object(["outcome": .string("accepted")])
    #expect(!attempt.uncertain)
}
