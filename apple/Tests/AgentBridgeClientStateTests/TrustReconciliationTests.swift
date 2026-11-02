import AgentBridgeAppleCore
@testable import AgentBridgeClientState
import Foundation
import Testing

private actor ReviewTrustHost {
    let stream = AsyncThrowingStream<BridgeEnvelope, any Error>.makeStream()
    var launches = 0
    func connection() -> HostTransport {
        HostTransport(messages: stream.stream, send: { request, _ in try await self.send(request) }, close: { await self.close() }, peerKey: nil)
    }

    func close() {
        stream.continuation.finish()
    }

    func send(_ request: BridgeRequest) throws -> BridgeEnvelope {
        var result: JSONValue = .object([:])
        if request.op == "hello" {
            result = .object(["protocol": .number(1), "capabilities": .array([.string("sessions.read"), .string("sessions.launch")]), "machine": .object(["id": .string("review-machine"), "instance": .string("review-run")])])
        } else if request.op == "session.launch" {
            launches += 1
            result = .object(["outcome": .string("unconfirmed"), "trust": .object(["id": .string("matching-question")])])
        }
        let response = try envelope(.object(["re": .number(Double(request.id)), "ok": .bool(true), "result": result]))
        stream.continuation.yield(response)
        if request.op == "sessions.subscribe" {
            try stream.continuation.yield(envelope(.object(["ev": .string("sessions.synced"), "seq": .number(1), "data": .object([:])])))
        }
        return response
    }

    private func envelope(_ value: JSONValue) throws -> BridgeEnvelope {
        try JSONDecoder().decode(BridgeEnvelope.self, from: JSONEncoder().encode(value))
    }
}

@MainActor @Test func reviewTrustReconciliationRequiresMatchingQuestionAndFinalOutcome() async throws {
    let fixture = ReviewTrustHost()
    let host = HostStore(id: "review-machine", name: "Review", isLocal: false) { await fixture.connection() }
    host.start()
    defer { host.stop() }
    for _ in 0 ..< 200 where !host.isLive {
        try await Task.sleep(for: .milliseconds(5))
    }
    try #require(host.isLive)
    let operation = BridgeOperation(op: "session.launch", data: ["request": .string(UUID().uuidString)])
    await host.perform(operation, slot: "launch")
    #expect(host.attempts["launch"]?.uncertain == true)
    host.reconcileLaunchTrust(questionID: "wrong-question", result: .object(["outcome": .string("started")]))
    #expect(host.attempts["launch"]?.uncertain == true)
    for outcome in ["refused", "unconfirmed", "future"] {
        host.reconcileLaunchTrust(questionID: "matching-question", result: .object(["outcome": .string(outcome)]))
        #expect(host.attempts["launch"]?.uncertain == true)
    }
    host.reconcileLaunchTrust(questionID: "matching-question", result: .object(["outcome": .string("started"), "session": .string("new-session")]))
    #expect(host.attempts["launch"]?.uncertain == false)
    #expect(host.attempts["launch"]?.result?["session"]?.string == "new-session")
    await host.perform(operation, slot: "launch")
    #expect(await fixture.launches == 2)
    host.reconcileLaunchTrust(questionID: "matching-question", result: .object(["outcome": .string("declined")]))
    #expect(host.attempts["launch"]?.uncertain == false)
    #expect(host.attempts["launch"]?.result?["outcome"]?.string == "failed")
}
