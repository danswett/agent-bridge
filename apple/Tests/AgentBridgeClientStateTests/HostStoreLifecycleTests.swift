import AgentBridgeAppleCore
@testable import AgentBridgeClientState
import Foundation
import Testing

private actor LifecycleHost {
    enum ReplyMode { case deliver, lose, hold }
    let instance: String
    let replyMode: ReplyMode
    let reportEvents: Bool
    let messages = AsyncThrowingStream<BridgeEnvelope, any Error>.makeStream()
    private(set) var requests: [BridgeRequest] = []
    private(set) var closeCount = 0
    private var sequence = 0
    private var revision = 1
    private var heldReply: CheckedContinuation<Void, Never>?
    private var heldUsage: CheckedContinuation<Void, Never>?

    init(instance: String, replyMode: ReplyMode = .deliver, reportEvents: Bool = false) {
        self.instance = instance
        self.replyMode = replyMode
        self.reportEvents = reportEvents
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

    func releaseUsage() {
        heldUsage?.resume(); heldUsage = nil
    }

    func count(_ op: String) -> Int {
        requests.filter { $0.op == op }.count
    }

    func skipEvent() throws {
        revision += 1
        sequence += 2
        try messages.continuation.yield(envelope(["ev": .string("future.event"), "seq": .number(Double(sequence)), "data": .object([:])]))
        if reportEvents {
            // Already queued by the old subscriptions before the client sees the gap.
            try reportEvent("usage.update", revision: revision - 1)
            try reportEvent("update.state", revision: revision - 1)
        }
    }

    private func send(_ request: BridgeRequest, timeout _: Duration) async throws -> BridgeEnvelope {
        requests.append(request)
        let result: JSONValue
        switch request.op {
        case "hello":
            result = .object(["protocol": .number(1), "capabilities": .array(["sessions.read", "sessions.reply", "usage", "update"].map(JSONValue.string)),
                              "machine": .object(["name": .string(instance), "id": .string("fixture"), "instance": .string(instance)])])
        case "usage.get", "usage.subscribe", "update.subscribe": result = .object(["revision": .number(Double(revision))])
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
        if request.op == "usage.get", reportEvents {
            revision += 1
            try reportEvent("usage.update", revision: revision)
            // The stream can advance before the awaiting caller is scheduled again.
            await withCheckedContinuation { heldUsage = $0 }
        }
        if request.op == "sessions.subscribe" {
            sequence += 1
            try messages.continuation.yield(envelope(["ev": .string("session.upsert"), "seq": .number(Double(sequence)), "data": .object([
                "id": .string("s1"), "title": .string("Revision \(revision)"), "agent": .string("codex"), "status": .string("idle"),
            ])]))
            sequence += 1
            try messages.continuation.yield(envelope(["ev": .string("sessions.synced"), "seq": .number(Double(sequence)), "data": .object([:])]))
        }
        if request.op == "update.subscribe" {
            // This marker follows both report responses in the shared stream. Seeing it
            // in HostStore proves those envelopes were consumed, regardless of scheduling.
            sequence += 1
            try messages.continuation.yield(envelope(["ev": .string("session.upsert"), "seq": .number(Double(sequence)), "data": .object([
                "id": .string("s1"), "title": .string("Revision \(revision)"), "agent": .string("codex"), "status": .string("idle"),
                "updatedAt": .string("reports-\(revision)"),
            ])]))
        }
        return response
    }

    private func reportEvent(_ name: String, revision: Int) throws {
        sequence += 1
        try messages.continuation.yield(envelope(["ev": .string(name), "seq": .number(Double(sequence)),
                                                  "data": .object(["revision": .number(Double(revision))])]))
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

@MainActor private func awaitReportBarrier(_ host: HostStore, revision: Int) async throws {
    // sessions.synced makes the session feed live before the separate usage/update
    // response envelopes drain. Wait for the later fixture marker, then assert
    // report values separately so a transient correct value cannot pass the test.
    try await awaitLifecycle {
        host.isLive && host.sessions.first?.title == "Revision \(revision)"
            && host.sessions.first?.updatedAt == "reports-\(revision)"
    }
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
    try await awaitReportBarrier(host, revision: 1)
    #expect(host.usage?["revision"] == .number(1))
    #expect(host.update?["revision"] == .number(1))
    try await fixture.skipEvent()
    try await awaitReportBarrier(host, revision: 2)
    #expect(host.usage?["revision"] == .number(2))
    #expect(host.update?["revision"] == .number(2))
    #expect(await fixture.count("sessions.subscribe") == 2)
    #expect(await fixture.count("usage.subscribe") == 2)
    #expect(await fixture.count("update.subscribe") == 2)
}

@MainActor @Test func queuedOldReportEventsCannotOverwriteResubscriptionSnapshots() async throws {
    let fixture = LifecycleHost(instance: "run", reportEvents: true)
    let host = HostStore(id: "fixture", name: "Fixture", isLocal: false) { await fixture.transport() }
    defer { host.stop() }
    host.start()
    try await awaitReportBarrier(host, revision: 1)
    try await fixture.skipEvent()
    try await awaitReportBarrier(host, revision: 2)
    #expect(host.usage?["revision"] == .number(2))
    #expect(host.update?["revision"] == .number(2))
    #expect(await fixture.count("usage.subscribe") == 2)
    #expect(await fixture.count("update.subscribe") == 2)
}

@MainActor @Test func newerUsageEventSurvivesADelayedRefreshContinuation() async throws {
    let fixture = LifecycleHost(instance: "run", reportEvents: true)
    let host = HostStore(id: "fixture", name: "Fixture", isLocal: false) { await fixture.transport() }
    defer { host.stop() }
    host.start()
    try await awaitLifecycle { host.isLive }
    let refreshing = Task { await host.refreshUsage() }
    try await awaitLifecycle { host.usage?["revision"] == .number(2) }
    await fixture.releaseUsage()
    await refreshing.value
    #expect(host.usage?["revision"] == .number(2))
}
