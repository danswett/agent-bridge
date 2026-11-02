@testable import AgentBridgeAppleCore
import Foundation
import Testing

@Test func requestUsesProtocolV1EnvelopeAndTypedData() throws {
    let data = try JSONEncoder().encode(BridgeRequest(id: 7, op: "usage.get", data: ["shared": .bool(true)]))
    let json = try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
    #expect(json["id"] as? Int == 7)
    #expect(json["op"] as? String == "usage.get")
    #expect((json["data"] as? [String: Any])?["shared"] as? Bool == true)
}

@Test func unknownSessionValuesUseProtocolFallbacks() throws {
    let json = try JSONDecoder().decode(JSONValue.self, from: Data(#"{"id":"a","agent":"future-agent","status":"new-status","cwd":"/workspace/demo","driver":"future"}"#.utf8))
    let session = try #require(BridgeSession(json: json))
    #expect(session.title == "/workspace/demo")
    #expect(session.status == "idle")
    #expect(session.agent == "unknown")
    #expect(session.driver == "person")
    #expect(BridgeSession(json: .object(["id": .string("")])) == nil)
}

@Test func mainConversationDoesNotMisattributeDelegateText() throws {
    let json = try JSONDecoder().decode(JSONValue.self, from: Data(#"{"id":"a","activity":{"response":"Delegate reply","reasoning":"Main reasoning","source":{"response":"delegate-1"},"own":{"response":"Main reply","responseTruncated":true}}}"#.utf8))
    let session = try #require(BridgeSession(json: json))
    #expect(session.response == "Main reply")
    #expect(session.reasoning == "Main reasoning")
    #expect(session.responseTruncated)
    let missingOwn = try JSONDecoder().decode(JSONValue.self, from: Data(#"{"id":"b","activity":{"response":"Delegate reply","source":{"response":"delegate-1"}}}"#.utf8))
    #expect(BridgeSession(json: missingOwn)?.response == nil)
}

@Test func backgroundWorkAndQuestionsKeepTheirMeaning() throws {
    let json = try JSONDecoder().decode(JSONValue.self, from: Data(#"{"id":"a","status":"idle","question":null,"activity":{"background":{"agents":2,"shells":1}}}"#.utf8))
    let session = try #require(BridgeSession(json: json))
    #expect(session.question == nil)
    #expect(session.status == "idle")
    #expect(session.statusText == "Delegating to 2 agents")
}
