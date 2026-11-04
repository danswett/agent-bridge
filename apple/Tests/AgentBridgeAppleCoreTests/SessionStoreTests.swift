@testable import AgentBridgeAppleCore
import Foundation
import Testing

private func envelope(_ json: String) throws -> BridgeEnvelope {
    try JSONDecoder().decode(BridgeEnvelope.self, from: Data(json.utf8))
}

private func event(_ name: String, seq: Int, data: String = "{}") throws -> BridgeEnvelope {
    try envelope(#"{"ev":"\#(name)","seq":\#(seq),"data":\#(data)}"#)
}

private func subscribe(_ store: inout SessionStore, id: Int = 1) throws {
    store.expectSubscription(id)
    try store.receive(envelope(#"{"re":\#(id),"ok":true,"result":{"count":1}}"#))
}

@Test func snapshotReplacesStaleSessions() throws {
    var store = SessionStore()
    try subscribe(&store)
    try store.receive(event("session.upsert", seq: 1, data: #"{"id":"old"}"#))
    try store.receive(event("sessions.synced", seq: 2))
    try subscribe(&store, id: 2)
    try store.receive(event("session.upsert", seq: 3, data: #"{"id":"new"}"#))
    #expect(store.sessions["old"] != nil)
    try store.receive(event("sessions.synced", seq: 4))
    #expect(store.sessions["old"] == nil)
    #expect(store.sessions["new"] != nil)
}

@Test func oldFeedBeforeSubscriptionResponseDoesNotEnterNewSnapshot() throws {
    var store = SessionStore()
    try subscribe(&store)
    try store.receive(event("sessions.synced", seq: 10))
    store.expectSubscription(7)
    try store.receive(event("session.upsert", seq: 11, data: #"{"id":"old"}"#))
    try store.receive(envelope(#"{"re":7,"ok":true,"result":{"count":1}}"#))
    try store.receive(event("session.upsert", seq: 12, data: #"{"id":"new"}"#))
    try store.receive(event("sessions.synced", seq: 13))
    #expect(store.orderedSessions.map(\.id) == ["new"])
}

@Test func sequenceGapRequestsResubscriptionAndQuarantinesLaterEvents() throws {
    var store = SessionStore()
    try subscribe(&store)
    try store.receive(event("session.upsert", seq: 40, data: #"{"id":"a"}"#))
    try store.receive(event("sessions.synced", seq: 41))
    #expect(try !store.receive(event("session.upsert", seq: 43, data: #"{"id":"b"}"#)))
    #expect(try store.receive(event("session.upsert", seq: 44, data: #"{"id":"c"}"#)))
    #expect(!store.isSynchronized)
    #expect(store.orderedSessions.map(\.id) == ["a"])
    try subscribe(&store, id: 2)
    try store.receive(event("sessions.synced", seq: 45))
    #expect(store.sessions.isEmpty)
    #expect(store.isSynchronized)
}

@Test func removalDropsSession() throws {
    var store = SessionStore()
    try subscribe(&store)
    try store.receive(event("session.upsert", seq: 1, data: #"{"id":"a"}"#))
    try store.receive(event("sessions.synced", seq: 2))
    try store.receive(event("session.remove", seq: 3, data: #"{"id":"a"}"#))
    #expect(store.sessions.isEmpty)
}

@Test func unknownEventsParticipateInChannelSequence() throws {
    var store = SessionStore()
    try subscribe(&store)
    try store.receive(event("sessions.synced", seq: 1))
    #expect(try store.receive(event("future.event", seq: 2)))
    #expect(try store.receive(event("session.upsert", seq: 3, data: #"{"id":"a"}"#)))
    #expect(store.isSynchronized)
    #expect(try !store.receive(event("future.event", seq: 3)))
}

@Test func snapshotClearsDiscoveryHoldAndKeepsStableRowOrder() throws {
    var store = SessionStore()
    try subscribe(&store)
    try store.receive(event("session.upsert", seq: 1, data: #"{"id":"b"}"#))
    try store.receive(event("session.upsert", seq: 2, data: #"{"id":"a"}"#))
    try store.receive(event("sessions.synced", seq: 3, data: #"{"discoveryHeld":{"reason":"Discovery uncertain"}}"#))
    #expect(store.discoveryHold == "Discovery uncertain")
    try subscribe(&store, id: 2)
    try store.receive(event("session.upsert", seq: 4, data: #"{"id":"a"}"#))
    try store.receive(event("session.upsert", seq: 5, data: #"{"id":"b"}"#))
    try store.receive(event("sessions.synced", seq: 6))
    #expect(store.discoveryHold == nil)
    #expect(store.orderedSessions.map(\.id) == ["b", "a"])
}
