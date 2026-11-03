import AgentBridgeAppleCore
@testable import AgentBridgeClientState
import Foundation
import Testing

private actor FixtureHost {
    let stream = AsyncThrowingStream<BridgeEnvelope, any Error>.makeStream()
    let instance: String
    let readOnlyReason: String?
    let capabilities: [String]
    let machineID: String
    var loseReply: Bool
    var mutationIDs: [String] = []
    var requests: [BridgeRequest] = []
    init(instance: String = "run-1", loseReply: Bool = false, readOnlyReason: String? = nil,
         capabilities: [String] = ["sessions.read", "sessions.reply"], machineID: String = "fixture")
    {
        self.instance = instance; self.loseReply = loseReply; self.machineID = machineID
        self.readOnlyReason = readOnlyReason; self.capabilities = capabilities
    }

    func connection() -> HostTransport {
        HostTransport(messages: stream.stream, send: { request, _ in try await self.send(request) },
                      close: { await self.close() }, peerKey: nil)
    }

    func close() {
        stream.continuation.finish(throwing: RelayError.disconnected)
    }

    func send(_ request: BridgeRequest) throws -> BridgeEnvelope {
        requests.append(request)
        let result: JSONValue
        switch request.op {
        case "hello":
            var machine: [String: JSONValue] = ["id": .string(machineID), "name": .string("Fixture"), "instance": .string(instance)]
            machine["readOnly"] = readOnlyReason.map(JSONValue.string)
            result = .object(["protocol": .number(1), "capabilities": .array(capabilities.map(JSONValue.string)), "machine": .object(machine)])
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

@MainActor @Test func aRelayHostClaimingAnotherMachinesIDIsRefusedBeforeItsSessionsAreRead() async throws {
    let impostor = FixtureHost(machineID: "this-mac")
    let host = HostStore(id: "fixture", name: "Fixture", isLocal: false) { await impostor.connection() }
    defer { host.stop() }
    host.start()
    try await until { host.status.hasSuffix("Reconnecting…") }
    #expect(!host.isLive)
    #expect(host.hello == nil)
    #expect(host.sessions.isEmpty)
    let requests = await impostor.requests
    #expect(!requests.contains { $0.op == "sessions.subscribe" })
}

@MainActor @Test func theLocalSocketHostIsNamedByItsHelloBecauseNoKeyNamesIt() async throws {
    let fixture = FixtureHost(machineID: "this-mac")
    let host = HostStore(id: "local", name: "This Mac", isLocal: true) { await fixture.connection() }
    defer { host.stop() }
    host.start()
    try await until { host.isLive }
    #expect(host.hello?["machine"]?["id"]?.string == "this-mac")
}

@MainActor @Test func automaticDisconnectExpiresAttachmentsForEverySessionAndKeepsReplyText() async throws {
    let fixture = FixtureHost()
    let host = HostStore(id: "fixture", name: "Fixture", isLocal: false) { await fixture.connection() }
    host.start()
    defer { host.stop() }
    try await until { host.isLive }
    let previousConnection = host.connectionID
    let drafts = ["s1": "Selected reply", "s2": "Another session's draft"]
    host.replyDrafts = drafts
    host.attachmentDrafts = ["s1": [(UUID(), "selected.txt")], "s2": [(UUID(), "background.pdf")]]
    var attachmentCountAtDisconnect: Int?
    host.onDisconnected = { [weak host] in attachmentCountAtDisconnect = host?.attachmentDrafts.count }

    await fixture.close()
    try await until { !host.isLive && host.connectionID != previousConnection }

    #expect(attachmentCountAtDisconnect == 0)
    #expect(host.attachmentDrafts.isEmpty)
    #expect(host.replyDrafts == drafts)
}

@MainActor @Test func uncertainReplyKeepsItsIdentityWhenTheSameHostReconnects() async throws {
    let first = FixtureHost(loseReply: true)
    let host = HostStore(id: "fixture", name: "Fixture", isLocal: false) { await first.connection() }
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
    let host = HostStore(id: "fixture", name: "Fixture", isLocal: false) { await first.connection() }
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

@MainActor @Test(arguments: [false, true])
func readOnlyReasonOverridesActionCapabilitiesButKeepsAdvertisedCapabilitiesAndReads(readOnly: Bool) async throws {
    let actions = ["sessions.answer", "sessions.reply", "sessions.approve", "sessions.stop",
                   "sessions.attach", "sessions.attach.blob", "sessions.attach.direct", "sessions.launch",
                   "sessions.transfer", "sessions.transfer.serve", "launch.trust", "update"]
    let reads = ["sessions.read", "usage", "usage.shared", "push"]
    let fixture = FixtureHost(readOnlyReason: readOnly ? "This host is read-only." : nil, capabilities: actions + reads)
    let host = HostStore(id: "fixture", name: "Fixture", isLocal: false) { await fixture.connection() }
    defer { host.stop() }
    host.start()
    try await until { host.isLive }
    #expect(host.capabilities == Set(actions + reads))
    for capability in actions {
        #expect(host.can(capability) == !readOnly)
    }
    for capability in reads {
        #expect(host.can(capability))
    }
    #expect(!host.can("not.advertised"))
    #expect(host.canOpenLauncher == !readOnly)
    host.stop()
    #expect(!host.canOpenLauncher)
}

@MainActor @Test func readOnlyHostDoesNotRecordOrSendANewMutationOrConsumeItsDraft() async throws {
    let fixture = FixtureHost(readOnlyReason: "This host is read-only.")
    let host = HostStore(id: "fixture", name: "Fixture", isLocal: false) { await fixture.connection() }
    defer { host.stop() }
    host.start()
    try await until { host.isLive }
    host.replyDrafts["s1"] = "Continue"
    var receipts = 0
    host.onMutationResult = { _, _ in receipts += 1 }
    let operation = try BridgeSessionOperations.reply(sessionID: "s1", text: "Continue", hasQuestion: false, capabilities: host.capabilities)
    await host.perform(operation, slot: "s1", consumesReplyDraft: true)
    #expect(host.attempts["s1"] == nil)
    #expect(await fixture.mutationIDs.isEmpty)
    #expect(host.replyDrafts["s1"] == "Continue")
    #expect(receipts == 0)
    // Read-only prevents mutations, not inspection or disposing of staged data.
    for op in ["session.get", "usage.get", "launch.options", "attachment.discard"] {
        _ = try await host.call(op)
    }
    #expect(await fixture.requests.suffix(4).map(\.op) == ["session.get", "usage.get", "launch.options", "attachment.discard"])
}

@MainActor @Test func readOnlyReconnectRetainsAnUncertainReplyUntilAnExplicitWritableRetry() async throws {
    let first = FixtureHost(loseReply: true)
    let host = HostStore(id: "fixture", name: "Fixture", isLocal: false) { await first.connection() }
    defer { host.stop() }
    host.start()
    try await until { host.isLive }
    host.replyDrafts["s1"] = "Continue"
    let operation = try BridgeSessionOperations.reply(sessionID: "s1", text: "Continue", hasQuestion: false, capabilities: host.capabilities)
    await host.perform(operation, slot: "s1", consumesReplyDraft: true)
    let error = host.attempts["s1"]?.error
    let second = FixtureHost(readOnlyReason: "This host is read-only.")
    host.reconnect { await second.connection() }
    try await until { host.isLive }
    await host.retry("s1")
    #expect(await second.mutationIDs.isEmpty)
    #expect(host.attempts["s1"]?.operation == operation)
    #expect(host.attempts["s1"]?.uncertain == true)
    #expect(host.attempts["s1"]?.isSending == false)
    #expect(host.attempts["s1"]?.error == error)
    #expect(host.replyDrafts["s1"] == "Continue")
    let third = FixtureHost()
    host.reconnect { await third.connection() }
    try await until { host.isLive }
    #expect(await third.mutationIDs.isEmpty)
    await host.retry("s1")
    #expect(host.attempts["s1"]?.result?["outcome"] == .string("delivered"))
    #expect(await first.mutationIDs == third.mutationIDs)
    #expect(host.replyDrafts["s1"] == "")
}
