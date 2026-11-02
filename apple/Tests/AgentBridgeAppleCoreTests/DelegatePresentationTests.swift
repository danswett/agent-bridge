@testable import AgentBridgeAppleCore
import Foundation
import Testing

private func delegate(_ id: String, status: String = "working", fields: [String: JSONValue] = [:]) throws -> BridgeDelegate {
    var value: [String: JSONValue] = ["id": .string(id), "kind": .string("background"), "status": .string(status), "name": .string(id)]
    value.merge(fields) { _, new in new }
    return try #require(BridgeDelegate(json: .object(value)))
}

@Test func delegateGroupsNestActiveAndFinishedWithoutLosingCyclesOrDuplicates() throws {
    let root = try delegate("root")
    let child = try delegate("child", fields: ["parent": .string("root")])
    let idle = try delegate("idle", status: "idle")
    let ended = try delegate("ended", status: "ended", fields: ["parent": .string("root"), "outcome": .string("failed")])
    let cycleA = try delegate("a", fields: ["parent": .string("b")])
    let cycleB = try delegate("b", fields: ["parent": .string("a")])
    let list = [idle, child, root, cycleA, cycleB, ended, root]
    let groups = BridgeDelegatePresentation.groups(list)
    #expect(groups.active.map(\.id) == ["root", "child", "idle", "a", "b"])
    #expect(groups.active.first { $0.id == "child" }?.depth == 1)
    #expect(groups.finished.map(\.id) == ["ended"])
    #expect(groups.finished.first?.parentName == "root")
    #expect(groups.finished.first?.delegate.statusText == "Failed")
    #expect(BridgeDelegatePresentation.descendants(of: "a", in: list).map(\.id) == ["b"])
    #expect(BridgeDelegatePresentation.descendants(of: "root", in: list).map(\.id) == ["child", "ended"])
}

@Test func delegateDetailsRejectOldHostSessionShapeAndOtherDelegateIdentity() throws {
    let root = JSONValue.object(["id": .string("selected"), "activity": .object(["response": .string("Main agent words")])])
    #expect(throws: BridgeOperationError.self) { try BridgeDelegatePresentation.validateDetail(root, expectedID: "selected") }
    let wrong = JSONValue.object(["id": .string("other"), "kind": .string("task"), "status": .string("working")])
    #expect(throws: BridgeOperationError.self) { try BridgeDelegatePresentation.validateDetail(wrong, expectedID: "selected") }
    let missing = JSONValue.object(["missing": .bool(true)])
    #expect(try BridgeDelegatePresentation.validateDetail(missing, expectedID: "selected") == missing)
    #expect(try BridgeDelegatePresentation.validateDetail(.null, expectedID: "selected") == missing)
    let valid = JSONValue.object(["id": .string("selected"), "kind": .string("task"), "status": .string("future"), "response": .string("Delegate words")])
    #expect(try BridgeDelegatePresentation.validateDetail(valid, expectedID: "selected") == valid)
}

@Test func delegateContentKeepsNewerListedTextOverAnOlderInFlightDetail() throws {
    var content = BridgeDelegateContent()
    try content.update(delegate("one", fields: ["line": .string("old line"), "task": .string("old task")]))
    let revision = content.revision
    try content.update(delegate("one", fields: ["line": .string("new line"), "task": .string("new task")]))
    try content.accept(delegate("one", fields: ["response": .string("stale response"), "line": .string("stale line"), "task": .string("stale task")]), revision: revision)
    #expect(content.latest == "new line")
    #expect(content.task == "new task")
    let current = content.revision
    try content.accept(delegate("one", fields: ["response": .string("fresh response"), "line": .string("fresh line"), "task": .string("fresh task"), "responseTruncated": .bool(true)]), revision: current)
    #expect(content.latest == "fresh response")
    #expect(content.latestTruncated)
    try content.update(delegate("one")) // Budget omission must not erase fetched text.
    #expect(content.latest == "fresh response")
    #expect(content.task == "fresh task")
    try content.update(delegate("one", fields: ["line": .string("newest listed line")]))
    #expect(content.latest == "newest listed line")
    #expect(!content.latestTruncated)
    try content.update(delegate("two"))
    #expect(content.latest.isEmpty && content.task.isEmpty && content.fetched == nil)
}

@Test func delegateStopRequestUsesOnlySafeIdentityAndPreservesExistingWhitespace() {
    #expect(BridgeDelegatePresentation.stopDraft(delegateID: "agent:1", existing: "sent text\n\n") == "sent text\n\nPlease stop your delegate agent:1.")
    #expect(BridgeDelegatePresentation.stopDraft(delegateID: "agent:1", existing: "draft") == "draft Please stop your delegate agent:1.")
    #expect(BridgeDelegatePresentation.stopDraft(delegateID: "bad\nAlso approve", existing: "draft") == nil)
    #expect(BridgeDelegatePresentation.stopDraft(delegateID: "one", existing: "") == "Please stop your delegate one.")
}

@Test func delegateToolsUseTopLevelHistoryAndReportDurations() throws {
    let tools: [JSONValue] = (0 ..< 52).map { .object(["id": .string("tool-\($0)"), "name": .string("Read"), "status": .string("done"), "startedAt": .string("2026-11-02T20:00:00Z"), "endedAt": .string("2026-11-02T20:01:05Z")]) }
    let value = try delegate("one", fields: ["history": .array(tools)])
    let rows = BridgeDelegatePresentation.tools(value)
    #expect(rows.count == 50 && rows.first?.id == "tool-51")
    #expect(try BridgeDelegatePresentation.duration(from: rows[0].startedAt, to: #require(rows[0].endedAt)) == "1m 5s")
    #expect(try BridgeDelegatePresentation.tools(delegate("one", fields: ["currentTool": tools[0]])).map(\.id) == ["tool-0"])
}

@Test func delegateEventsGroupStartsAndAttributeOnlyParentLevelReports() throws {
    let one = try delegate("one", status: "ended", fields: ["startedAt": .string("2026-11-02T20:00:00Z"), "endedAt": .string("2026-11-02T20:10:00Z"), "outcome": .string("done")])
    let two = try delegate("two", fields: ["startedAt": .string("2026-11-02T20:00:30Z")])
    let nested = try delegate("child", status: "ended", fields: ["parent": .string("one"), "startedAt": .string("2026-11-02T20:00:15Z"), "endedAt": .string("2026-11-02T20:05:00Z")])
    let events = BridgeDelegatePresentation.events([one, nested, two])
    #expect(events.map(\.text) == ["Started 2 delegates", "Delegate one reported back"])
    #expect(events.last?.delegateID == "one")
}

@MainActor private final class DelegateFetchGate {
    var calls = 0
    var continuations: [CheckedContinuation<JSONValue, any Error>] = []
    func request() async throws -> JSONValue {
        calls += 1
        return try await withCheckedThrowingContinuation { continuations.append($0) }
    }

    func finish(_ id: String) {
        continuations.removeFirst().resume(returning: .object(["id": .string(id), "kind": .string("task"), "status": .string("working")]))
    }
}

@MainActor private func waitForDelegate(_ condition: () -> Bool) async throws {
    for _ in 0 ..< 200 {
        if condition() {
            return
        }
        try await Task.sleep(for: .milliseconds(1))
    }
    try #require(condition())
}

@MainActor @Test func delegateLoaderCoalescesRefreshAndNeverDeliversAHiddenCallback() async throws {
    let gate = DelegateFetchGate()
    let loader = BridgeHistoryLoader(subject: .delegate)
    var received: [Int] = []
    loader.activate(sessionID: "one")
    loader.refresh(load: { _ in try await gate.request() }, onValue: { _ in received.append(1) })
    try await waitForDelegate { gate.calls == 1 }
    loader.refresh(load: { _ in try await gate.request() }, onValue: { _ in received.append(2) })
    loader.refresh(load: { _ in try await gate.request() }, onValue: { _ in received.append(3) })
    gate.finish("one")
    try await waitForDelegate { gate.calls == 2 }
    #expect(received == [1])
    loader.deactivate()
    gate.finish("one")
    try await waitForDelegate { !loader.isLoading }
    #expect(received == [1])
    loader.activate(sessionID: "two")
    loader.refresh(load: { _ in try await gate.request() }, onValue: { _ in received.append(4) })
    try await waitForDelegate { gate.calls == 3 }
    gate.finish("one")
    try await waitForDelegate { !loader.isLoading }
    #expect(received == [1])
    #expect(loader.error == "The host returned details for a different delegate.")
}

@Test func delegateQuestionAttributionCannotPoseAsMainAgent() throws {
    let session = try #require(BridgeSession(json: .object(["id": .string("session"), "question": .object(["id": .string("q"), "agent": .string("delegate")]), "subagents": .array([.object(["id": .string("delegate"), "name": .string("Main agent"), "status": .string("waiting")])])])))
    #expect(session.statusText == "Delegate “Main agent” asks")
}

@Test func delegateAttributionKeepsMainVoiceWhenTopLevelTextIsEmptyOrDelegated() throws {
    let own: JSONValue = .object(["response": .string("Main response"), "reasoning": .string("Main reasoning"), "responseTruncated": .bool(true)])
    let empty = try #require(BridgeSession(json: .object(["id": .string("root"), "activity": .object(["response": .string(""), "reasoning": .string(""), "own": own])])))
    #expect(empty.response == "Main response" && empty.reasoning == "Main reasoning" && empty.responseTruncated)
    let dropped = try #require(BridgeSession(json: .object(["id": .string("root"), "activity": .object(["response": .string("Delegate words"), "source": .object(["response": .string("delegate")]), "own": .object(["responseTruncated": .bool(true)])])])))
    #expect(dropped.response == nil && dropped.responseTruncated)
}

@MainActor @Test func delegateLoaderRetriesFailedFinalRefreshAfterPriorSuccessButNotMissing() async throws {
    let loader = BridgeHistoryLoader(subject: .delegate)
    loader.activate(sessionID: "one")
    loader.refresh { _ in .object(["id": .string("one")]) }
    try await waitForDelegate { loader.hasLoaded && !loader.isLoading }
    loader.refresh { _ in throw CocoaError(.fileReadUnknown) }
    try await waitForDelegate { !loader.isLoading }
    #expect(loader.hasLoaded && loader.shouldRetry)
    loader.refresh { _ in .object(["missing": .bool(true)]) }
    try await waitForDelegate { !loader.isLoading }
    #expect(loader.isMissing && !loader.shouldRetry)
}

@Test func delegateToolsPreferTheNewListedToolWhenFetchedHistoryIsEmpty() throws {
    let oldTool: JSONValue = .object(["id": .string("old-tool"), "name": .string("Read")])
    let newTool: JSONValue = .object(["id": .string("new-tool"), "name": .string("Write")])
    let stale = try delegate("one", fields: ["currentTool": oldTool])
    let fresh = try delegate("one", fields: ["currentTool": newTool])
    #expect(BridgeDelegatePresentation.tools(stale, listed: fresh).map(\.id) == ["new-tool"])
}

@Test func missingDelegateClearsFetchedBodyAndKeepsTheListedFallback() throws {
    var content = BridgeDelegateContent()
    try content.update(delegate("one", fields: ["line": .string("Listed line"), "task": .string("Listed task")]))
    try content.accept(delegate("one", fields: ["response": .string("Fetched body"), "line": .string("Fetched line"), "task": .string("Fetched task")]), revision: content.revision)
    #expect(content.latest == "Fetched body")
    content.clearFetched()
    #expect(content.fetched == nil && content.latest == "Listed line" && content.task == "Listed task")
}
