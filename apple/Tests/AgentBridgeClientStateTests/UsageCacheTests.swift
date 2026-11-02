import AgentBridgeAppleCore
@testable import AgentBridgeClientState
import Foundation
import Testing

private let cacheNow = Date(timeIntervalSince1970: 1_793_300_000)

private func usageReport(account: String = "person", measuredAt: Date = cacheNow, percent: Double? = 42, error: String? = nil, readBy: String? = nil) -> BridgeUsageReport {
    let formatter = ISO8601DateFormatter()
    var entry: [String: JSONValue] = ["agent": .string("copilot"), "account": .string(account), "label": .string("GitHub Copilot"), "source": .string("api"), "measuredAt": .string(formatter.string(from: measuredAt)),
                                      "windows": .array(percent.map { [.object(["key": .string("plan"), "label": .string("Plan"), "percent": .number($0)])] } ?? [])]
    if let error {
        entry["error"] = .string(error)
    }
    if let readBy {
        entry["readBy"] = .object(["id": .string(readBy), "name": .string("Peer")])
    }
    return BridgeUsageReport(json: .object(["agents": .array([.object(entry)])]))
}

private func usageDirectory() throws -> URL {
    let url = FileManager.default.temporaryDirectory.appendingPathComponent("bridge-usage-cache-\(UUID().uuidString)", isDirectory: true)
    try FileManager.default.createDirectory(at: url, withIntermediateDirectories: false)
    return url
}

@Test func usageCacheKeepsTheReadingBesideANewerFailureAcrossRuns() async throws {
    let directory = try usageDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let cache = BridgeUsageCache(directory: directory)
    let session = try await cache.activate(relay: "https://relay.example", account: "account-1", now: cacheNow)
    _ = try await cache.merge(report: usageReport(), machineID: "m", machineName: "Mac", allowedMachineIDs: ["m"], session: session, now: cacheNow)
    _ = try await cache.merge(report: usageReport(measuredAt: cacheNow.addingTimeInterval(30), percent: nil, error: "Refresh failed"), machineID: "m", machineName: "Mac", allowedMachineIDs: ["m"], session: session, now: cacheNow.addingTimeInterval(30))
    let reopened = BridgeUsageCache(directory: directory)
    let next = try await reopened.activate(relay: "https://relay.example", account: "account-1", now: cacheNow.addingTimeInterval(60))
    let result = try await reopened.report(session: next, now: cacheNow.addingTimeInterval(60))
    #expect(result["agents"]?.array?.count == 2)
    let rows = BridgeUsageReport.merged([BridgeUsageReport(json: result)])
    #expect(rows.first?.headline == 42)
    #expect(rows.first?.measuredAt == cacheNow)
    #expect(rows.first?.error == "Refresh failed")
    #expect(rows.first?.readByName == "Mac")
}

@Test func usageCacheIsScopedToBothRelayAndAccountAndRevokesOldLeases() async throws {
    let directory = try usageDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let cache = BridgeUsageCache(directory: directory)
    let first = try await cache.activate(relay: "https://relay.example", account: "one", now: cacheNow)
    _ = try await cache.merge(report: usageReport(), machineID: "m", machineName: "Mac", allowedMachineIDs: ["m"], session: first, now: cacheNow)
    let second = try await cache.activate(relay: "https://relay.example", account: "two", now: cacheNow)
    #expect(try await cache.report(session: second, now: cacheNow)["agents"]?.array?.isEmpty == true)
    await #expect(throws: BridgeUsageCache.Failure.self) { try await cache.report(session: first, now: cacheNow) }
    let otherRelay = try await cache.activate(relay: "https://other.example", account: "one", now: cacheNow)
    #expect(try await cache.report(session: otherRelay, now: cacheNow)["agents"]?.array?.isEmpty == true)
}

@Test func signoutDeletesCachedUsageAndCannotBeUndoneByALateHostUpdate() async throws {
    let directory = try usageDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let cache = BridgeUsageCache(directory: directory)
    let session = try await cache.activate(relay: "https://relay.example", account: "one", now: cacheNow)
    _ = try await cache.merge(report: usageReport(), machineID: "m", machineName: "Mac", allowedMachineIDs: ["m"], session: session, now: cacheNow)
    try await cache.clear(session: session)
    await #expect(throws: BridgeUsageCache.Failure.self) { try await cache.merge(report: usageReport(), machineID: "m", machineName: "Mac", allowedMachineIDs: ["m"], session: session, now: cacheNow) }
    #expect(try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil).isEmpty)
}

@Test func expiredUsageCannotHideANewerFailureOrSurviveItsFourteenDayLimit() async throws {
    let directory = try usageDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let cache = BridgeUsageCache(directory: directory)
    let session = try await cache.activate(relay: "https://relay.example", account: "one", now: cacheNow)
    _ = try await cache.merge(report: usageReport(measuredAt: cacheNow.addingTimeInterval(-BridgeUsageCache.retention + 1)), machineID: "m", machineName: "Mac", allowedMachineIDs: ["m"], session: session, now: cacheNow)
    let result = try await cache.merge(report: usageReport(measuredAt: cacheNow, percent: nil, error: "No current reading"), machineID: "m", machineName: "Mac", allowedMachineIDs: ["m"], session: session, now: cacheNow.addingTimeInterval(2))
    #expect(result["agents"]?.array?.count == 1)
    #expect(BridgeUsageReport(json: result).agents.first?.windows.isEmpty == true)
    #expect(try await cache.report(session: session, now: cacheNow.addingTimeInterval(BridgeUsageCache.retention + 1))["agents"]?.array?.isEmpty == true)
}

@Test func usageCacheRejectsUnknownPeersAndDropsForgottenMachines() async throws {
    let directory = try usageDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let cache = BridgeUsageCache(directory: directory)
    let session = try await cache.activate(relay: "https://relay.example", account: "one", now: cacheNow)
    let unlisted = try await cache.merge(report: usageReport(readBy: "stranger"), machineID: "m", machineName: "Mac", allowedMachineIDs: ["m"], session: session, now: cacheNow)
    #expect(unlisted["agents"]?.array?.isEmpty == true)
    _ = try await cache.merge(report: usageReport(readBy: "peer"), machineID: "m", machineName: "Mac", allowedMachineIDs: ["m", "peer"], session: session, now: cacheNow)
    try await cache.forget(machineID: "peer", session: session)
    #expect(try await cache.report(session: session, now: cacheNow)["agents"]?.array?.isEmpty == true)
}

@Test func corruptedCacheReportsAnErrorWithoutBlockingLiveUsageOrSignout() async throws {
    let directory = try usageDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let cache = BridgeUsageCache(directory: directory)
    let first = try await cache.activate(relay: "https://relay.example", account: "one", now: cacheNow)
    _ = try await cache.merge(report: usageReport(), machineID: "m", machineName: "Mac", allowedMachineIDs: ["m"], session: first, now: cacheNow)
    let path = try #require(FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil).first)
    try Data("bad json".utf8).write(to: path, options: .atomic)
    let next = try await cache.activate(relay: "https://relay.example", account: "one", now: cacheNow)
    #expect(await cache.statusMessage != nil)
    #expect(try await cache.report(session: next, now: cacheNow)["agents"]?.array?.isEmpty == true)
    try await cache.clear(session: next)
    #expect(try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil).isEmpty)
}

@Test func usageCacheKeepsAtMostThirtyTwoAllowancesAndRestrictsFilePermissions() async throws {
    let directory = try usageDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let cache = BridgeUsageCache(directory: directory)
    let session = try await cache.activate(relay: "https://relay.example", account: "one", now: cacheNow)
    for index in 0 ..< 40 {
        _ = try await cache.merge(report: usageReport(account: "account-\(index)", measuredAt: cacheNow.addingTimeInterval(Double(index))), machineID: "m", machineName: "Mac", allowedMachineIDs: ["m"], session: session, now: cacheNow.addingTimeInterval(40))
    }
    let report = try await cache.report(session: session, now: cacheNow.addingTimeInterval(40))
    #expect(report["agents"]?.array?.count == 32)
    #expect(!BridgeUsageReport(json: report).agents.contains { $0.account == "account-0" })
    let file = try #require(FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil).first)
    let attributes = try FileManager.default.attributesOfItem(atPath: file.path)
    #expect((attributes[.posixPermissions] as? NSNumber)?.intValue == 0o600)
}
