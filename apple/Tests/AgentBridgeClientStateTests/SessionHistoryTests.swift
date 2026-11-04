import AgentBridgeAppleCore
@testable import AgentBridgeClientState
import Foundation
import Testing

@MainActor @Test func pinsSurviveMoreThan300SessionsFromOtherMachines() throws {
    let defaults = UserDefaults.standard
    let keys = ["bridge.local.local.order", "bridge.local.local.read", "bridge.local.local.pins"]
    let previous = keys.map { defaults.object(forKey: $0) }
    defer { for (key, value) in zip(keys, previous) {
        defaults.set(value, forKey: key)
    } }
    let store = BridgeStore()
    let oldHost = HostStore(id: "old", name: "Old", isLocal: false) { throw RelayError.disconnected }
    let newHost = HostStore(id: "new", name: "New", isLocal: false) { throw RelayError.disconnected }
    let old = try #require(BridgeSession(json: .object(["id": .string("pinned")])))
    store.registerSessions([old], host: oldHost)
    store.togglePin("old:pinned")
    let newer = try (0 ..< 300).map { index in
        try #require(BridgeSession(json: .object(["id": .string("session\(index)")])))
    }
    store.registerSessions(newer, host: newHost)
    #expect(store.manualOrder.count == 300)
    #expect(!store.manualOrder.contains("old:pinned"))
    #expect(store.pinned.contains("old:pinned"))
    #expect(defaults.stringArray(forKey: "bridge.local.local.pins")?.contains("old:pinned") == true)
}
