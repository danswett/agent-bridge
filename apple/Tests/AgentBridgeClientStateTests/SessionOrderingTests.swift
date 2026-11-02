import AgentBridgeAppleCore
@testable import AgentBridgeClientState
import Testing

@MainActor private func orderingEntry(_ id: String, updatedAt: String? = nil, host: HostStore? = nil) throws -> SessionEntry {
    var value: [String: JSONValue] = ["id": .string(id)]
    if let updatedAt {
        value["updatedAt"] = .string(updatedAt)
    }
    return try SessionEntry(session: #require(BridgeSession(json: .object(value))), host: host)
}

@MainActor @Test func manualSessionOrderKeepsPinsFirstAndUsesTheFirstSavedOccurrence() throws {
    let entries = try ["a", "b", "c", "d", "e"].map { try orderingEntry($0) }
    let order = ["sample:b", "sample:a", "sample:b"]
    let sorted = SessionOrdering.sorted(entries.reversed(), pinned: ["sample:c"], manualOrder: order, sortOrder: "manual")
    #expect(sorted.map(\.session.id) == ["c", "b", "a", "d", "e"])
    #expect(SessionOrdering.sorted([], pinned: [], manualOrder: order, sortOrder: "manual").isEmpty)
}

@MainActor @Test func recentSessionOrderRetainsPinnedPriorityAndManualTies() throws {
    let entries = try [orderingEntry("a", updatedAt: "2026-11-01"), orderingEntry("b", updatedAt: "2026-11-02"),
                       orderingEntry("c", updatedAt: "2026-11-02"), orderingEntry("d")]
    let sorted = SessionOrdering.sorted(entries, pinned: ["sample:d"], manualOrder: ["sample:c", "sample:b", "sample:a"], sortOrder: "recent")
    #expect(sorted.map(\.session.id) == ["d", "c", "b", "a"])
}

@MainActor @Test func machineSessionOrderKeepsNaturalMachineNamesAndManualTies() throws {
    let first = HostStore(id: "first", name: "Build 2", isLocal: false) { throw RelayError.disconnected }
    let second = HostStore(id: "second", name: "Build 10", isLocal: false) { throw RelayError.disconnected }
    let entries = try [orderingEntry("a", host: second), orderingEntry("a", host: first), orderingEntry("b", host: first)]
    let sorted = SessionOrdering.sorted(entries, pinned: [], manualOrder: ["second:a", "first:b", "first:a"], sortOrder: "machine")
    #expect(sorted.map(\.id) == ["first:b", "first:a", "second:a"])
}
