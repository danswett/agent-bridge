import AgentBridgeAppleCore
@testable import AgentBridgeClientState
import Testing

@MainActor @Test func selectionFindsOnlyTheRequestedSessionAndPreservesHistory() throws {
    let sessions = try ["one", "two"].map { id in
        try #require(BridgeSession(json: .object(["id": .string(id)])))
    }
    let selected = try #require(SessionEntry.find(id: "sample:two", in: sessions, host: nil, ended: true))
    #expect(selected.session.id == "two")
    #expect(selected.ended)
    #expect(SessionEntry.find(id: "sample:missing", in: sessions, host: nil) == nil)
}
