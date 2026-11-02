import AgentBridgeAppleCore
@testable import AgentBridgeClientState
import Foundation
import Testing

private actor LifecycleHost {
    enum ReplyMode { case deliver, lose, hold }
    let instance: String
    let replyMode: ReplyMode
    let messages = AsyncThrowingStream<BridgeEnvelope, any Error>.makeStream()
    private(set) var requests: [BridgeRequest] = []
    private(set) var closeCount = 0
    private var sequence = 0
    private var revision = 1
    private var heldReply: CheckedContinuation<Void, Never>?

    init(instance: String, replyMode: ReplyMode = .deliver) {
        self.instance = instance
        self.replyMode = replyMode
    }

    func transport() -> HostTransport {
        HostTransport(messages: messages.stream, send: { try await self.send($0, timeout: $1) },
                      close: { await self.close() }, peerKey: nil)
    }

    func close() {
        closeCount += 1; messages.continuation.finish(throwing: RelayError.disconnected)
    }

    func releaseReply() {
        heldReply?.resume(); heldReply = nil
    }

    func count(_ op: String) -> Int {
        requests.filter { $0.op == op }.count
    }

    func skipEvent() throws {
        revision += 1
        sequence += 2
        try messages.continuation.yield(envelope(["ev": .string("future.event"), "seq": .number(Double(sequence)), "data": .object([:])]))
    }

    private func send(_ request: BridgeRequest, timeout _: Duration) async throws -> BridgeEnvelope {
        requests.append(request)
        let result: JSONValue
        switch request.op {
        case "hello":
            result = .object(["protocol": .number(1), "capabilities": .array(["sessions.read", "sessions.reply", "usage", "update"].map(JSONValue.string)),
                              "machine": .object(["name": .string(instance), "id": .string("fixture"), "instance": .string(instance)])])
        case "usage.subscribe", "update.subscribe": result = .object(["revision": .number(Double(revision))])
        case "session.reply":
            if replyMode == .lose {
                close(); throw RelayError.disconnected
            }
            if replyMode == .hold {
                await withCheckedContinuation { heldReply = $0 }
            }
            result = .object(["outcome": .string("delivered")])
        default: result = .object([:])
        }
        let response = try envelope(["re": .number(Double(request.id)), "ok": .bool(true), "result": result])
        messages.continuation.yield(response)
        if request.op == "sessions.subscribe" {
            sequence += 1
            try messages.continuation.yield(envelope(["ev": .string("session.upsert"), "seq": .number(Double(sequence)), "data": .object([
                "id": .string("s1"), "title": .string("Revision \(revision)"), "agent": .string("codex"), "status": .string("idle"),
            ])]))
            sequence += 1
            try messages.continuation.yield(envelope(["ev": .string("sessions.synced"), "seq": .number(Double(sequence)), "data": .object([:])]))
        }
        return response
    }

    private func envelope(_ dictionary: [String: JSONValue]) throws -> BridgeEnvelope {
        try JSONDecoder().decode(BridgeEnvelope.self, from: JSONEncoder().encode(JSONValue.object(dictionary)))
    }
}

private actor DeferredProvider {
    let transport: HostTransport
    private(set) var started = false
    private var continuation: CheckedContinuation<HostTransport, Never>?
    init(_ transport: HostTransport) {
        self.transport = transport
    }

    func open() async -> HostTransport {
        started = true
        return await withCheckedContinuation { continuation = $0 }
    }

    func release() {
        continuation?.resume(returning: transport); continuation = nil
    }
}

@MainActor private func awaitLifecycle(_ condition: @MainActor () async -> Bool) async throws {
    for _ in 0 ..< 200 {
        if await condition() {
            return
        }
        try await Task.sleep(for: .milliseconds(5))
    }
    throw RelayError.timeout
}

@MainActor @Test func delayedOldProviderCannotReplaceTheNewConnection() async throws {
    let old = LifecycleHost(instance: "old")
    let deferred = await DeferredProvider(old.transport())
    let current = LifecycleHost(instance: "current")
    let host = HostStore(id: "fixture", name: "Fixture", isLocal: false) { await deferred.open() }
    defer { host.stop() }
    host.start()
    try await awaitLifecycle { await deferred.started }
    host.reconnect { await current.transport() }
    try await awaitLifecycle { host.isLive }
    let connection = host.connectionID
    await deferred.release()
    try await awaitLifecycle { await old.closeCount > 0 }
    #expect(host.instance == "current")
    #expect(host.connectionID == connection)
    #expect(host.isLive)
    #expect(await current.closeCount == 0)
    #expect(await old.count("hello") == 0)
}

@MainActor @Test func lateMutationResultDoesNotConfirmDeliveryOnAReplacementConnection() async throws {
    let old = LifecycleHost(instance: "run", replyMode: .hold)
    let current = LifecycleHost(instance: "run")
    let host = HostStore(id: "fixture", name: "Fixture", isLocal: false) { await old.transport() }
    defer { host.stop() }
    host.start()
    try await awaitLifecycle { host.isLive }
    let operation = try BridgeSessionOperations.reply(sessionID: "s1", text: "Continue", hasQuestion: false, capabilities: host.capabilities)
    let sending = Task { await host.perform(operation, slot: "s1") }
    try await awaitLifecycle { await old.count("session.reply") == 1 }
    host.reconnect { await current.transport() }
    try await awaitLifecycle { host.isLive }
    await old.releaseReply()
    await sending.value
    #expect(host.attempts["s1"]?.uncertain == true)
    #expect(host.attempts["s1"]?.result == nil)
    #expect(host.attempts["s1"]?.operation == operation)
    #expect(await current.count("session.reply") == 0)
    #expect(host.isLive)
}

@MainActor @Test func reconnectDoesNotAutomaticallyReplayAnUncertainMutation() async throws {
    let first = LifecycleHost(instance: "run", replyMode: .lose)
    let second = LifecycleHost(instance: "run")
    let host = HostStore(id: "fixture", name: "Fixture", isLocal: false) { await first.transport() }
    defer { host.stop() }
    host.start()
    try await awaitLifecycle { host.isLive }
    let operation = try BridgeSessionOperations.reply(sessionID: "s1", text: "Continue", hasQuestion: false, capabilities: host.capabilities)
    await host.perform(operation, slot: "s1")
    host.reconnect { await second.transport() }
    try await awaitLifecycle { host.isLive }
    #expect(host.attempts["s1"]?.operation == operation)
    try await host.perform(BridgeSessionOperations.reply(sessionID: "s1", text: "New text", hasQuestion: false, capabilities: host.capabilities), slot: "s1")
    #expect(await second.count("session.reply") == 0)
    #expect(host.attempts["s1"]?.operation == operation)
}

@MainActor @Test func sequenceGapRefreshesSessionsUsageAndUpdateState() async throws {
    let fixture = LifecycleHost(instance: "run")
    let host = HostStore(id: "fixture", name: "Fixture", isLocal: false) { await fixture.transport() }
    defer { host.stop() }
    host.start()
    try await awaitLifecycle { host.isLive }
    #expect(host.usage?["revision"] == .number(1))
    #expect(host.update?["revision"] == .number(1))
    try await fixture.skipEvent()
    try await awaitLifecycle { host.isLive && host.sessions.first?.title == "Revision 2" }
    #expect(host.usage?["revision"] == .number(2))
    #expect(host.update?["revision"] == .number(2))
    #expect(await fixture.count("sessions.subscribe") == 2)
    #expect(await fixture.count("usage.subscribe") == 2)
    #expect(await fixture.count("update.subscribe") == 2)
}
