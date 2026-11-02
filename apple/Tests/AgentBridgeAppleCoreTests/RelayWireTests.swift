@testable import AgentBridgeAppleCore
import Foundation
import Testing

@Test func relayDeviceIdentityMatchesThePublishedStaticKeyHash() throws {
    let publicKey = try RelayWire.unhex("04e85d7f4bd2670d434ce4f1e2e44a9e325c885446eda6a2ea54a277da740c1efc6c16065e9abc53d827a17247f9895800ee6c5cb31432bd469b33bd7367d2154f")
    #expect(try RelayWire.deviceID(publicKey: publicKey) == "ffe2dce9f4226e45a4a8c48027435db4")
}

@Test func relayProofMatchesTheUpstreamLabelAndExactByteConcatenation() throws {
    // Independently computed with Python hashlib/hmac, matching Wire.ProofMac.
    let mac = try RelayWire.proofMAC(sharedSecret: Data(0 ..< 32), nonce: Data(32 ..< 64), accountID: "github:123456")
    #expect(RelayWire.hex(mac) == "cf12630f18637142c05a46fe6b8e41d71b1383abb448663bb825330b32a04348")
    #expect(throws: RelayWire.Failure.invalidProof) {
        try RelayWire.proofMAC(sharedSecret: Data(repeating: 0, count: 31), nonce: Data(repeating: 0, count: 32), accountID: "account")
    }
    #expect(throws: RelayWire.Failure.invalidProof) {
        try RelayWire.proofMAC(sharedSecret: Data(repeating: 0, count: 32), nonce: Data(), accountID: "account")
    }
    #expect(throws: RelayWire.Failure.invalidProof) {
        try RelayWire.proofMAC(sharedSecret: Data(repeating: 0, count: 32), nonce: Data(repeating: 0, count: 32), accountID: "")
    }
}

@Test func relayFramesCarryPeerChannelKindAndBodyAtTheExactWireOffsets() throws {
    let peer = "ffe2dce9f4226e45a4a8c48027435db4"
    let channel = try RelayWire.unhex("0011223344556677")
    let body = Data([0, 1, 2, 255])
    let frame = try RelayWire.frame(peerID: peer, kind: .transport, channelID: channel, body: body)
    #expect(RelayWire.hex(frame) == "01ffe2dce9f4226e45a4a8c48027435db4040011223344556677000102ff")
    let parsed = try RelayWire.parseFrame(frame)
    #expect(parsed.peerID == peer)
    #expect(parsed.kind == .transport)
    #expect(parsed.channelID == channel)
    #expect(parsed.body == body)
    let close = try RelayWire.frame(peerID: peer, kind: .close, channelID: channel, body: Data())
    #expect(close.count == 26)
    #expect(try RelayWire.parseFrame(close).kind == .close)
    let largest = try RelayWire.frame(peerID: peer, kind: .transport, channelID: channel,
                                      body: Data(repeating: 0, count: NoiseXXHandshake.maximumMessageSize))
    #expect(largest.count == RelayWire.maximumFrameSize)
    #expect(try RelayWire.parseFrame(largest).body.count == NoiseXXHandshake.maximumMessageSize)
}

@Test func relayFramesRejectBadIDsMalformedHeadersUnknownKindsAndOversizeBodies() throws {
    let peer = String(repeating: "a", count: 32)
    let channel = Data(repeating: 0, count: 8)
    #expect(throws: RelayWire.Failure.invalidDeviceID) {
        try RelayWire.frame(peerID: "not-a-device", kind: .msg1, channelID: channel, body: Data())
    }
    #expect(throws: RelayWire.Failure.invalidChannelID) {
        try RelayWire.frame(peerID: peer, kind: .msg1, channelID: channel.dropLast(), body: Data())
    }
    #expect(throws: RelayWire.Failure.invalidFrame) {
        try RelayWire.frame(peerID: peer, kind: .close, channelID: channel, body: Data([0]))
    }
    #expect(throws: RelayWire.Failure.invalidFrame) {
        try RelayWire.frame(peerID: peer, kind: .transport, channelID: channel, body: Data(repeating: 0, count: 65536))
    }
    let valid = try RelayWire.frame(peerID: peer, kind: .close, channelID: channel, body: Data())
    var badPrefix = valid
    badPrefix[0] = 2
    var badKind = valid
    badKind[17] = 99
    for invalid in [valid.dropLast(), badPrefix, badKind, valid + Data([0]), Data(repeating: 0, count: 65562)] {
        #expect(throws: RelayWire.Failure.invalidFrame) { try RelayWire.parseFrame(invalid) }
    }
}

@Test func relayPrologueBindsTheOrderedDeviceIDsAndRawEightByteChannelID() throws {
    let first = String(repeating: "a", count: 32)
    let second = String(repeating: "b", count: 32)
    let channel = Data(0 ..< 8)
    #expect(try RelayWire.prologue(initiator: first, responder: second, channelID: channel)
        == Data("agent-bridge-s4|\(first)|\(second)|".utf8) + channel)
    #expect(try RelayWire.prologue(initiator: first.uppercased(), responder: second, channelID: channel)
        == RelayWire.prologue(initiator: first, responder: second, channelID: channel))
    #expect(try RelayWire.prologue(initiator: first, responder: second, channelID: channel)
        != RelayWire.prologue(initiator: second, responder: first, channelID: channel))
    #expect(throws: RelayWire.Failure.invalidDeviceID) { try RelayWire.prologue(initiator: "", responder: second, channelID: channel) }
    #expect(throws: RelayWire.Failure.invalidChannelID) { try RelayWire.prologue(initiator: first, responder: second, channelID: Data()) }
}

@Test func relayHexDecoderRejectsOddNonASCIIAndNonHexInput() throws {
    #expect(try RelayWire.unhex("00aAfF") == Data([0, 170, 255]))
    #expect(try RelayWire.unhex("") == Data())
    #expect(RelayWire.hex(Data([0, 170, 255])) == "00aaff")
    for input in ["0", "0g", " 0", "０１", "ab\n", "💻"] {
        #expect(throws: RelayWire.Failure.invalidHex) { try RelayWire.unhex(input) }
    }
    #expect(!RelayWire.isDeviceID(String(repeating: "z", count: 32)))
    #expect(!RelayWire.isDeviceID(String(repeating: "a", count: 31)))
    #expect(RelayWire.isDeviceID(String(repeating: "F", count: 32)))
}
