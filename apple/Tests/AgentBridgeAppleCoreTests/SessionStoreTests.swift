import Foundation
import Testing
@testable import AgentBridgeAppleCore

private func event(_ name: String, seq: Int, data: String) throws -> BridgeEnvelope {
    let json = #"{"ev":"\#(name)","seq":\#(seq),"data":\#(data)}"#
    return try JSONDecoder().decode(BridgeEnvelope.self, from: Data(json.utf8))
}

@Test func snapshotReplacesStaleSessions() throws {
    var store = SessionStore()
    store.beginSubscription()
    #expect(store.receive(try event("session.upsert", seq: 1, data: #"{"id":"old"}"#)))
    #expect(store.receive(try event("sessions.synced", seq: 2, data: "{}")))
    #expect(store.sessions.count == 1)
    store.beginSubscription()
    #expect(store.receive(try event("session.upsert", seq: 9, data: #"{"id":"new"}"#)))
    #expect(store.receive(try event("sessions.synced", seq: 10, data: "{}")))
    #expect(store.sessions["old"] == nil)
    #expect(store.sessions["new"] != nil)
}

@Test func sequenceGapRequestsResubscription() throws {
    var store = SessionStore()
    store.beginSubscription()
    #expect(store.receive(try event("session.upsert", seq: 40, data: #"{"id":"a"}"#)))
    #expect(!store.receive(try event("session.upsert", seq: 42, data: #"{"id":"b"}"#)))
}

@Test func removalDropsSession() throws {
    var store = SessionStore()
    store.beginSubscription()
    #expect(store.receive(try event("session.upsert", seq: 1, data: #"{"id":"a"}"#)))
    #expect(store.receive(try event("sessions.synced", seq: 2, data: "{}")))
    #expect(store.receive(try event("session.remove", seq: 3, data: #"{"id":"a"}"#)))
    #expect(store.sessions.isEmpty)
}
