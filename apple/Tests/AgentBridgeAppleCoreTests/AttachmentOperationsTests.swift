@testable import AgentBridgeAppleCore
import Foundation
import Testing

@Test func attachmentUsesSHA256AndResumesOnlyAtTheHostsAcknowledgedOffset() throws {
    let capabilities: Set = ["sessions.reply", "sessions.attach"]
    let file = try BridgeAttachment(name: "test.txt", type: "text/plain", bytes: Data("prefixabc".utf8).dropFirst(6), capabilities: capabilities, isLocal: false)
    #expect(file.sha256 == "ungWv48Bz-pBQUDeXa4iI7ADYaOWF3qctBD_YfIAFa0")
    #expect(file.start.data["attachment"]?.string == file.id.uuidString.lowercased())
    let start = try BridgeAttachmentState(json: .object(["received": .number(0), "complete": .bool(false)]))
    let chunk = try #require(try file.chunk(after: start))
    #expect(chunk.data["offset"] == .number(0))
    #expect(chunk.data["data"] == .string("YWJj"))
    #expect(try file.chunk(after: start) == chunk)
    let finished = try BridgeAttachmentState(json: .object(["received": .number(3), "complete": .bool(true)]))
    #expect(try file.chunk(after: finished) == nil)
    let corrupt = try BridgeAttachmentState(json: .object(["received": .number(2), "complete": .bool(true)]))
    #expect(throws: BridgeOperationError.self) { try file.chunk(after: corrupt) }
}

@Test func attachmentChunksStayBoundedAndDirectCapabilityAppliesOnlyLocally() throws {
    let capabilities: Set = ["sessions.reply", "sessions.attach", "sessions.attach.direct"]
    let bytes = Data(repeating: 255, count: 256 * 1024 + 1)
    #expect(throws: BridgeOperationError.self) { try BridgeAttachment(name: "file.bin", bytes: bytes, capabilities: capabilities, isLocal: false) }
    let file = try BridgeAttachment(name: "file.bin", bytes: bytes, capabilities: capabilities, isLocal: true)
    var received = 0
    var assembled = Data()
    while received < bytes.count {
        let state = try BridgeAttachmentState(json: .object(["received": .number(Double(received)), "complete": .bool(false)]))
        let operation = try #require(try file.chunk(after: state))
        let encoded = try #require(operation.data["data"]?.string)
        let payload = try #require(Data(base64Encoded: encoded))
        #expect(payload.count <= 11 * 1024)
        #expect(try JSONEncoder().encode(operation.request(id: 1)).count < 16 * 1024)
        assembled.append(payload)
        received += payload.count
    }
    #expect(assembled == bytes)
    #expect(throws: BridgeOperationError.self) { try BridgeAttachmentState(json: .object(["received": .number(-1), "complete": .bool(false)])) }
    #expect(throws: BridgeOperationError.self) { try BridgeAttachmentState(json: .object(["received": .number(0.5), "complete": .bool(false)])) }
}

@Test func attachmentOnlyRepliesAreAllowedButDuplicateReferencesAreNot() throws {
    let capabilities: Set = ["sessions.reply", "sessions.attach"]
    let id = UUID()
    let reply = try BridgeSessionOperations.reply(sessionID: "s", text: "", attachments: [id], hasQuestion: false, capabilities: capabilities)
    #expect(reply.data["attachments"]?.array?.count == 1)
    #expect(throws: BridgeOperationError.self) { try BridgeSessionOperations.reply(sessionID: "s", text: "", attachments: [id, id], hasQuestion: false, capabilities: capabilities) }
}
