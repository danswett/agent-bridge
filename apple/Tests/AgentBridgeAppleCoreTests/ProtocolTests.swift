import Foundation
import Testing
@testable import AgentBridgeAppleCore

@Test func requestUsesProtocolV1Envelope() throws {
    let data = try JSONEncoder().encode(BridgeRequest(id: 7, op: "hello"))
    let json = try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
    #expect(json["id"] as? Int == 7)
    #expect(json["op"] as? String == "hello")
}

@Test func unknownSessionStatusRemainsReadable() throws {
    let json = try JSONDecoder().decode(JSONValue.self, from: Data(#"{"id":"a","agent":"future-agent","status":"new-status","workspace":"demo"}"#.utf8))
    let session = try #require(BridgeSession(json: json))
    #expect(session.id == "a")
    #expect(session.title == "demo")
}
