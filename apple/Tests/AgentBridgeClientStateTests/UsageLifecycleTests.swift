import AgentBridgeAppleCore
@testable import AgentBridgeClientState
import Foundation
import Testing

private actor UsageActivationGate {
    private var entered = false
    private var entryWaiters: [CheckedContinuation<Void, Never>] = []
    private var releaseWaiter: CheckedContinuation<Void, Never>?

    func hold() async {
        entered = true
        entryWaiters.forEach { $0.resume() }
        entryWaiters = []
        await withCheckedContinuation { releaseWaiter = $0 }
    }

    func waitUntilEntered() async {
        if entered {
            return
        }
        await withCheckedContinuation { entryWaiters.append($0) }
    }

    func release() {
        releaseWaiter?.resume(); releaseWaiter = nil
    }
}

private func lifecycleDirectory() throws -> URL {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent("bridge-usage-lifecycle-\(UUID().uuidString)", isDirectory: true)
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false)
    return directory
}

private func lifecycleReport(now: Date) -> BridgeUsageReport {
    BridgeUsageReport(json: .object(["agents": .array([.object([
        "agent": .string("copilot"), "label": .string("Copilot"), "source": .string("api"),
        "measuredAt": .string(ISO8601DateFormatter().string(from: now)),
        "windows": .array([.object(["key": .string("plan"), "label": .string("Plan"), "percent": .number(42)])]),
    ])])]))
}

private func seedUsage(_ cache: BridgeUsageCache, relay: String = "https://relay.example", account: String, now: Date) async throws -> BridgeUsageCacheSession {
    let session = try await cache.activate(relay: relay, account: account, now: now)
    _ = try await cache.merge(report: lifecycleReport(now: now), machineID: "fixture", machineName: "Fixture",
                              allowedMachineIDs: ["fixture"], session: session, now: now)
    return session
}

@MainActor @Test func aDelayedUsageActivationCannotReplaceTheNewAccount() async throws {
    let directory = try lifecycleDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let cache = BridgeUsageCache(directory: directory)
    let gate = UsageActivationGate()
    let lifecycle = BridgeUsageLifecycle(activate: { relay, account in
        if account == "old" {
            await gate.hold()
        }
        return try await cache.activate(relay: relay, account: account)
    }, clear: { try await cache.clearScope(relay: $0, account: $1) })
    let old = lifecycle.activate(relay: "https://relay.example", account: "old")
    await gate.waitUntilEntered()
    let current = lifecycle.activate(relay: "https://relay.example", account: "new")
    await gate.release()
    #expect(try await old.value == nil)
    let lease = try #require(try await current.value)
    #expect(lease.account == "new")
    #expect(try await cache.report(session: lease)["agents"]?.array?.isEmpty == true)
}

@MainActor @Test func signoutWaitsForInFlightActivationThenDeletesItsImmutableScope() async throws {
    let directory = try lifecycleDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let cache = BridgeUsageCache(directory: directory)
    let previousLease = try await seedUsage(cache, account: "one", now: Date())
    let gate = UsageActivationGate()
    let lifecycle = BridgeUsageLifecycle(activate: { relay, account in
        await gate.hold()
        return try await cache.activate(relay: relay, account: account)
    }, clear: { try await cache.clearScope(relay: $0, account: $1) })
    let activation = lifecycle.activate(relay: "https://relay.example", account: "one")
    await gate.waitUntilEntered()
    let clearing = lifecycle.clear(relay: "https://relay.example", account: "one")
    await gate.release()
    #expect(try await activation.value == nil)
    try await clearing.value
    #expect(try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil).isEmpty)
    await #expect(throws: BridgeUsageCache.Failure.self) { try await cache.report(session: previousLease) }
    let reopened = try await cache.activate(relay: "https://relay.example", account: "one")
    #expect(try await cache.report(session: reopened)["agents"]?.array?.isEmpty == true)
}

@MainActor @Test func clearingTheOldAccountDoesNotRevokeTheNewAccountOrLeaveOldFiles() async throws {
    let directory = try lifecycleDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let cache = BridgeUsageCache(directory: directory)
    _ = try await seedUsage(cache, account: "old", now: Date())
    let lifecycle = BridgeUsageLifecycle(cache: cache)
    let current = lifecycle.activate(relay: "https://relay.example", account: "new")
    let clearing = lifecycle.clear(relay: "https://relay.example", account: "old")
    let lease = try #require(try await current.value)
    try await clearing.value
    #expect(lease.account == "new")
    #expect(try await cache.report(session: lease)["agents"]?.array?.isEmpty == true)
    #expect(try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil).isEmpty)
}

@Test func clearingAScopeWorksAfterItsLeaseRotatesAndPreservesOtherScopes() async throws {
    let directory = try lifecycleDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let cache = BridgeUsageCache(directory: directory)
    let old = try await seedUsage(cache, account: "one", now: Date())
    let rotated = try await cache.activate(relay: "https://relay.example", account: "one")
    await #expect(throws: BridgeUsageCache.Failure.self) { try await cache.clear(session: old) }
    try await cache.clearScope(relay: old.relay, account: old.account)
    await #expect(throws: BridgeUsageCache.Failure.self) { try await cache.report(session: rotated) }
    #expect(try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil).isEmpty)
    _ = try await seedUsage(cache, account: "one", now: Date())
    let other = try await seedUsage(cache, relay: "https://other.example", account: "one", now: Date())
    try await cache.clearScope(relay: "https://relay.example", account: "one")
    #expect(try await cache.report(session: other)["agents"]?.array?.count == 1)
    #expect(try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil).count == 1)
}

@MainActor @Test func deactivatingAQueuedUsageRequestPreventsItFromOpeningTheCache() async throws {
    let directory = try lifecycleDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let cache = BridgeUsageCache(directory: directory)
    let lifecycle = BridgeUsageLifecycle(cache: cache)
    let operation = lifecycle.activate(relay: "https://relay.example", account: "one")
    lifecycle.deactivate()
    #expect(try await operation.value == nil)
    #expect(try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil).isEmpty)
}
