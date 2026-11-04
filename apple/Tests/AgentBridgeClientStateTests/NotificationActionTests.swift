import AgentBridgeAppleCore
@testable import AgentBridgeClientState
import Foundation
import Testing
import UserNotifications

private let actionCaps: Set<String> = ["sessions.read", "sessions.answer", "sessions.approve", "sessions.reply"]
private func actionSession(questionID: String? = "q", message: String = "Continue?", version: String = "v1", response: String = "Finished", kind: String = "approval", allowOther: Bool = false, extraField: Bool = false) -> JSONValue {
    var data: [String: JSONValue] = ["id": .string("s"), "title": .string("Private session"), "agent": .string("copilot"),
                                     "status": .string(questionID == nil ? "idle" : "waiting"), "updatedAt": .string(version), "activity": .object(["response": .string(response)])]
    if let questionID {
        var fields: [JSONValue] = [.object(["name": .string("decision"), "type": .string("choice"), "allowOther": .bool(allowOther),
                                            "options": .array([.object(["value": .string("approve"), "label": .string("Approve")]), .object(["value": .string("deny"), "label": .string("Deny")])])])]
        if extraField {
            fields.append(.object(["name": .string("details"), "type": .string("text")]))
        }
        data["question"] = .object(["id": .string(questionID), "message": .string(message), "kind": .string(kind), "fields": .array(fields)])
    }
    return .object(data)
}

private func actionNotice(_ raw: JSONValue = actionSession(), previews: Bool = true, capabilities: Set<String> = actionCaps) throws -> BridgeSessionNotice {
    let session = try #require(BridgeSession(json: raw))
    let before = try session.question == nil ? [#require(BridgeSession(json: .object(["id": .string("s"), "agent": .string("copilot"), "status": .string("working")])))] : []
    return try #require(BridgeNotificationPlanner.plan(machineID: "m", machineName: "Mac", before: before, after: [session], showPreviews: previews, capabilities: capabilities).notices.first)
}

private func actionRequest(_ notice: BridgeSessionNotice, text: String? = nil) throws -> BridgeNoticeActionRequest {
    try .init(notice: notice, action: #require(notice.actions.first), scope: "account-one", text: text, requestID: UUID())
}

@Test func notificationChoicesRequirePreviewsAndRejectFullFormsOtherAndDisabledApprovals() throws {
    #expect(try actionNotice(previews: false).actions.isEmpty)
    #expect(try actionNotice(capabilities: ["sessions.answer"]).actions.isEmpty)
    #expect(try actionNotice(actionSession(allowOther: true)).actions.isEmpty)
    #expect(try actionNotice(actionSession(extraField: true)).actions.isEmpty)
    #expect(try actionNotice(actionSession(message: String(repeating: "long ", count: 100))).actions.isEmpty)
    #expect(try actionNotice().actions.map(\.title) == ["Approve Once", "Deny"])
    #expect(try actionNotice(actionSession(kind: "question")).actions.count == 2)
    #expect(try actionNotice(actionSession(questionID: nil), previews: false).actions.map(\.title) == ["Reply"])
}

@Test func notificationOperationBindsTheWholeQuestionAccountSessionVersionAndCapabilities() throws {
    let notice = try actionNotice()
    let request = try actionRequest(notice)
    let current = try #require(BridgeSession(json: actionSession()))
    let operation = try BridgeNotificationActions.operation(request, current: current, scope: "account-one", capabilities: actionCaps, isLive: true)
    #expect(operation.op == "session.answer")
    #expect(operation.requestID == request.requestID)
    #expect(operation.data["question"] == .string("q"))
    for raw in [actionSession(questionID: "replacement"), actionSession(message: "Different permission"), actionSession(version: "v2")] {
        let changed = try #require(BridgeSession(json: raw))
        #expect(throws: BridgeOperationError.self) { try BridgeNotificationActions.operation(request, current: changed, scope: "account-one", capabilities: actionCaps, isLive: true) }
    }
    #expect(throws: BridgeOperationError.self) { try BridgeNotificationActions.operation(request, current: current, scope: "other-account", capabilities: actionCaps, isLive: true) }
    #expect(throws: BridgeOperationError.self) { try BridgeNotificationActions.operation(request, current: current, scope: "account-one", capabilities: actionCaps, isLive: false) }
    #expect(throws: BridgeOperationError.self) { try BridgeNotificationActions.operation(request, current: current, scope: "account-one", capabilities: ["sessions.answer"], isLive: true) }
}

@Test func notificationRepliesRequireTheSameFinishedTurnAndNonemptyText() throws {
    let notice = try actionNotice(actionSession(questionID: nil))
    let request = try actionRequest(notice, text: "Continue")
    let current = try #require(BridgeSession(json: actionSession(questionID: nil)))
    #expect(try BridgeNotificationActions.operation(request, current: current, scope: "account-one", capabilities: actionCaps, isLive: true).op == "session.reply")
    for raw in [actionSession(), actionSession(questionID: nil, version: "v2"), actionSession(questionID: nil, response: "Next turn")] {
        let changed = try #require(BridgeSession(json: raw))
        #expect(throws: BridgeOperationError.self) { try BridgeNotificationActions.operation(request, current: changed, scope: "account-one", capabilities: actionCaps, isLive: true) }
    }
    let blank = try actionRequest(notice, text: " ")
    #expect(throws: BridgeOperationError.self) { try BridgeNotificationActions.operation(blank, current: current, scope: "account-one", capabilities: actionCaps, isLive: true) }
}

@Test func notificationTokensCannotBeReplayedRecreatedOrCarriedIntoAnotherAccount() throws {
    let notice = try actionNotice()
    var registry = BridgeNoticeActionRegistry()
    registry.register(notice, scope: "account-one")
    let first = try #require(registry.bindings[notice.id])
    #expect(registry.consume(identifier: notice.id, token: UUID().uuidString, machine: "m", session: "s", action: "choice.0", text: nil) == nil)
    #expect(registry.consume(identifier: notice.id, token: first.token.uuidString, machine: "other", session: "s", action: "choice.0", text: nil) == nil)
    let consumed = registry.consume(identifier: notice.id, token: first.token.uuidString, machine: "m", session: "s", action: "choice.0", text: nil)
    let press = try #require(consumed)
    #expect(press.requestID == first.requestID)
    #expect(registry.consume(identifier: notice.id, token: first.token.uuidString, machine: "m", session: "s", action: "choice.0", text: nil) == nil)
    registry.register(notice, scope: "account-two")
    #expect(registry.consume(identifier: notice.id, token: first.token.uuidString, machine: "m", session: "s", action: "choice.0", text: nil) == nil)
    registry.remove(notice.id)
    #expect(registry.bindings.isEmpty)
}

@MainActor @Test func nativeNotificationActionsRequireForegroundAndUnlockAndUseTextInputForReplies() throws {
    let category = try BridgeNotificationManager.category(notice: actionNotice(), token: UUID())
    #expect(category.actions.allSatisfy { $0.options.contains(.foreground) && $0.options.contains(.authenticationRequired) })
    let reply = try BridgeNotificationManager.category(notice: actionNotice(actionSession(questionID: nil)), token: UUID())
    #expect(reply.actions.first is UNTextInputNotificationAction)
}

private actor NoticeHost {
    let stream = AsyncThrowingStream<BridgeEnvelope, any Error>.makeStream()
    var fresh: JSONValue
    let initial: JSONValue
    var holdGet: Bool
    let outcome: String
    private(set) var operations: [BridgeRequest] = []
    private var gate: CheckedContinuation<Void, Never>?
    init(fresh: JSONValue = actionSession(), initial: JSONValue = actionSession(), holdGet: Bool = false, outcome: String = "delivered") {
        self.fresh = fresh; self.initial = initial; self.holdGet = holdGet; self.outcome = outcome
    }

    func connection() -> HostTransport {
        HostTransport(messages: stream.stream, send: { request, _ in try await self.send(request) }, close: { await self.close() }, peerKey: nil)
    }

    func close() {
        stream.continuation.finish(throwing: RelayError.disconnected)
    }

    func release() {
        gate?.resume(); gate = nil
    }

    func send(_ request: BridgeRequest) async throws -> BridgeEnvelope {
        operations.append(request)
        let result: JSONValue
        switch request.op {
        case "hello": result = .object(["protocol": .number(1), "capabilities": .array(actionCaps.map(JSONValue.string)), "machine": .object(["id": .string("m"), "instance": .string("run")])])
        case "session.get":
            if holdGet {
                await withCheckedContinuation { gate = $0 }
            }
            result = fresh
        case "session.answer", "session.reply": result = .object(["outcome": .string(outcome)])
        default: result = .object([:])
        }
        let response = try envelope(["re": .number(Double(request.id)), "ok": .bool(true), "result": result])
        stream.continuation.yield(response)
        if request.op == "sessions.subscribe" {
            try stream.continuation.yield(envelope(["ev": .string("session.upsert"), "seq": .number(1), "data": initial]))
            try stream.continuation.yield(envelope(["ev": .string("sessions.synced"), "seq": .number(2), "data": .object([:])]))
        }
        return response
    }

    private func envelope(_ value: [String: JSONValue]) throws -> BridgeEnvelope {
        try JSONDecoder().decode(BridgeEnvelope.self, from: JSONEncoder().encode(JSONValue.object(value)))
    }
}

@MainActor private func awaitNotice(_ condition: @MainActor () async -> Bool) async throws {
    for _ in 0 ..< 200 {
        if await condition() {
            return
        }; try await Task.sleep(for: .milliseconds(5))
    }
    throw RelayError.timeout
}

@MainActor @Test func notificationDeliveryChecksTheHostThenUsesTheOriginalPressIdentity() async throws {
    let fixture = NoticeHost()
    let host = HostStore(id: "m", name: "Mac", isLocal: false) { await fixture.connection() }
    defer { host.stop() }; host.start()
    try await awaitNotice { host.isLive }
    let request = try actionRequest(actionNotice())
    let result = await BridgeNotificationDelivery.perform(request, host: host, scope: "account-one") { true }
    #expect(result == "Delivered.")
    let operations = await fixture.operations
    #expect(operations.suffix(2).map(\.op) == ["session.get", "session.answer"])
    #expect(operations.last?.data["request"] == .string(request.requestID.uuidString.lowercased()))
}

@MainActor @Test func notificationDeliveryRejectsAChangedFreshQuestionWithoutSending() async throws {
    let fixture = NoticeHost(fresh: actionSession(questionID: "replacement"))
    let host = HostStore(id: "m", name: "Mac", isLocal: false) { await fixture.connection() }
    defer { host.stop() }; host.start()
    try await awaitNotice { host.isLive }
    let result = try await BridgeNotificationDelivery.perform(actionRequest(actionNotice()), host: host, scope: "account-one") { true }
    #expect(result.hasPrefix("Not sent"))
    #expect(await fixture.operations.filter { $0.op == "session.answer" }.isEmpty)
}

@MainActor @Test func notificationDeliveryDoesNotQueueAnActionWhenAccountChangesDuringFreshRead() async throws {
    let fixture = NoticeHost(holdGet: true)
    let host = HostStore(id: "m", name: "Mac", isLocal: false) { await fixture.connection() }
    defer { host.stop() }; host.start()
    try await awaitNotice { host.isLive }
    let request = try actionRequest(actionNotice())
    var current = true
    let sending = Task { await BridgeNotificationDelivery.perform(request, host: host, scope: "account-one") { current } }
    try await awaitNotice { await fixture.operations.contains { $0.op == "session.get" } }
    current = false
    await fixture.release()
    #expect(await sending.value.hasPrefix("Not sent"))
    #expect(await fixture.operations.filter { $0.op == "session.answer" }.isEmpty)
}

@MainActor @Test func notificationDeliveryNeverOverridesAnUncertainPressOrSendsWhileOffline() async throws {
    let fixture = NoticeHost(outcome: "unconfirmed")
    let host = HostStore(id: "m", name: "Mac", isLocal: false) { await fixture.connection() }
    defer { host.stop() }; host.start()
    try await awaitNotice { host.isLive }
    let request = try actionRequest(actionNotice())
    let operation = try BridgeNotificationActions.operation(request, current: host.sessions[0], scope: "account-one", capabilities: actionCaps, isLive: true)
    await host.perform(operation, slot: "session:s")
    let before = await fixture.operations.count
    #expect(host.attempts["session:s"]?.uncertain == true)
    let blocked = try await BridgeNotificationDelivery.perform(actionRequest(actionNotice()), host: host, scope: "account-one") { true }
    #expect(blocked.hasPrefix("Not sent"))
    #expect(await fixture.operations.count == before)
    host.stop()
    let offline = await BridgeNotificationDelivery.perform(request, host: host, scope: "account-one") { true }
    #expect(offline.hasPrefix("Not sent"))
    #expect(await fixture.operations.count == before)
}

@MainActor @Test func notificationReplyDoesNotConsumeAMatchingSavedComposerDraft() async throws {
    let session = actionSession(questionID: nil)
    let fixture = NoticeHost(fresh: session, initial: session)
    let host = HostStore(id: "m", name: "Mac", isLocal: false) { await fixture.connection() }
    defer { host.stop() }; host.start()
    try await awaitNotice { host.isLive }
    host.replyDrafts["s"] = "Continue with tests"
    let request = try actionRequest(actionNotice(session), text: "Continue")
    let result = await BridgeNotificationDelivery.perform(request, host: host, scope: "account-one") { true }
    #expect(result == "Delivered.")
    #expect(host.replyDrafts["s"] == "Continue with tests")
    #expect(await fixture.operations.last?.data["text"] == .string("Continue"))
}
