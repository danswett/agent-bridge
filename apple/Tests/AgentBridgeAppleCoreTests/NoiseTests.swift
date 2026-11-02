@testable import AgentBridgeAppleCore
import Foundation
import Testing

private struct NoiseVectors: Decodable {
    struct Key: Decodable { let d: String; let pub: String }
    struct Message: Decodable { let from: String; let payload: String; let message: String }
    let `protocol`: String
    let prologue: String
    let keys: [String: Key]
    let handshake: [Message]
    let handshakeHash: String
    let transport: [Message]

    static func load() throws -> Self {
        let url = try #require(Bundle.module.url(forResource: "noise-xx-p256-vectors", withExtension: "json"))
        return try JSONDecoder().decode(Self.self, from: Data(contentsOf: url))
    }

    func key(_ name: String) throws -> NoiseKeyPair {
        let fixture = try #require(keys[name])
        let key = try NoiseKeyPair(rawRepresentation: RelayWire.unhex(fixture.d))
        #expect(try key.publicKey == (RelayWire.unhex(fixture.pub)))
        return key
    }
}

private struct NoisePair {
    var initiator: NoiseXXHandshake
    var responder: NoiseXXHandshake

    init() throws {
        let first = NoiseKeyPair()
        let second = NoiseKeyPair()
        initiator = try NoiseXXHandshake(role: .initiator, staticKey: first, prologue: Data(), expectedPeerID: second.deviceID)
        responder = try NoiseXXHandshake(role: .responder, staticKey: second, prologue: Data(), expectedPeerID: first.deviceID)
    }

    mutating func complete() throws {
        _ = try responder.readMessage(initiator.writeMessage())
        _ = try initiator.readMessage(responder.writeMessage())
        _ = try responder.readMessage(initiator.writeMessage())
    }
}

@Test func noiseMatchesEveryDotNetAndWebCryptoHandshakeAndTransportVector() throws {
    let vectors = try NoiseVectors.load()
    #expect(vectors.protocol == NoiseXXHandshake.protocolName)
    let firstKey = try vectors.key("initStatic")
    let secondKey = try vectors.key("respStatic")
    let prologue = try RelayWire.unhex(vectors.prologue)
    var first = try NoiseXXHandshake(role: .initiator, staticKey: firstKey, prologue: prologue,
                                     expectedPeerID: secondKey.deviceID, fixedEphemeral: vectors.key("initEphemeral"))
    var second = try NoiseXXHandshake(role: .responder, staticKey: secondKey, prologue: prologue,
                                      expectedPeerID: firstKey.deviceID, fixedEphemeral: vectors.key("respEphemeral"))
    for vector in vectors.handshake {
        let payload = try RelayWire.unhex(vector.payload)
        let expected = try RelayWire.unhex(vector.message)
        if vector.from == "i" {
            let message = try first.writeMessage(payload)
            #expect(message == expected)
            #expect(try second.readMessage(message) == payload)
        } else {
            let message = try second.writeMessage(payload)
            #expect(message == expected)
            #expect(try first.readMessage(message) == payload)
        }
    }
    #expect(first.isComplete && second.isComplete)
    #expect(try first.handshakeHash == (RelayWire.unhex(vectors.handshakeHash)))
    #expect(first.handshakeHash == second.handshakeHash)
    #expect(first.remoteStaticKey == secondKey.publicKey)
    #expect(second.remoteStaticKey == firstKey.publicKey)
    for vector in vectors.transport {
        let payload = try RelayWire.unhex(vector.payload)
        let expected = try RelayWire.unhex(vector.message)
        if vector.from == "i" {
            let message = try first.encrypt(payload)
            #expect(message == expected)
            #expect(try second.decrypt(message) == payload)
        } else {
            let message = try second.encrypt(payload)
            #expect(message == expected)
            #expect(try first.decrypt(message) == payload)
        }
    }
}

@Test func noiseAgreementRejectsMalformedOffCurveAndOutOfFieldKeys() throws {
    let first = NoiseKeyPair()
    let second = NoiseKeyPair()
    #expect(try first.sharedSecret(with: second.publicKey) == second.sharedSecret(with: first.publicKey))
    #expect(try NoiseKeyPair(rawRepresentation: first.rawRepresentation).publicKey == first.publicKey)
    var offCurve = second.publicKey
    offCurve[64] ^= 1
    let prime = try RelayWire.unhex("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff")
    let invalid = [Data(), second.publicKey.dropFirst(), Data([4]) + Data(repeating: 0, count: 64),
                   offCurve, Data([4]) + prime + second.publicKey.suffix(32)]
    for bytes in invalid {
        #expect(throws: NoiseFailure.invalidPublicKey) { try first.sharedSecret(with: bytes) }
        #expect(throws: NoiseFailure.invalidPublicKey) { try RelayWire.deviceID(publicKey: bytes) }
    }
}

@Test func noiseChecksTurnOrderAndRequiresCompletedHandshakeBeforeTransport() throws {
    var pair = try NoisePair()
    #expect(throws: NoiseFailure.invalidState) { try pair.responder.writeMessage() }
    #expect(throws: NoiseFailure.invalidState) { try pair.initiator.readMessage(Data()) }
    #expect(throws: NoiseFailure.invalidState) { try pair.initiator.encrypt(Data()) }
    #expect(throws: NoiseFailure.invalidState) { try pair.initiator.decrypt(Data()) }
    try pair.complete()
    #expect(throws: NoiseFailure.invalidState) { try pair.initiator.writeMessage() }
    #expect(throws: NoiseFailure.invalidState) { try pair.responder.readMessage(Data()) }
}

@Test func noiseRejectsWrongHostIdentityBeforeSendingItsStaticKey() throws {
    let first = NoiseKeyPair()
    let second = NoiseKeyPair()
    var initiator = try NoiseXXHandshake(role: .initiator, staticKey: first, prologue: Data(), expectedPeerID: NoiseKeyPair().deviceID)
    var responder = try NoiseXXHandshake(role: .responder, staticKey: second, prologue: Data(), expectedPeerID: first.deviceID)
    _ = try responder.readMessage(initiator.writeMessage())
    let response = try responder.writeMessage()
    #expect(throws: NoiseFailure.peerMismatch) { try initiator.readMessage(response) }
    #expect(throws: NoiseFailure.failed) { try initiator.writeMessage() }
    #expect(initiator.remoteStaticKey == nil)
    #expect(!initiator.isComplete)
}

@Test func noiseResponderAlsoRejectsWrongClientIdentity() throws {
    let first = NoiseKeyPair()
    let second = NoiseKeyPair()
    var initiator = try NoiseXXHandshake(role: .initiator, staticKey: first, prologue: Data(), expectedPeerID: second.deviceID)
    var responder = try NoiseXXHandshake(role: .responder, staticKey: second, prologue: Data(), expectedPeerID: NoiseKeyPair().deviceID)
    _ = try responder.readMessage(initiator.writeMessage())
    _ = try initiator.readMessage(responder.writeMessage())
    let final = try initiator.writeMessage()
    #expect(throws: NoiseFailure.peerMismatch) { try responder.readMessage(final) }
    #expect(throws: NoiseFailure.failed) { try responder.readMessage(final) }
}

@Test func noiseTamperedHandshakePermanentlyFailsTheChannel() throws {
    var pair = try NoisePair()
    _ = try pair.responder.readMessage(pair.initiator.writeMessage())
    let original = try pair.responder.writeMessage(Data("introduction".utf8))
    var tampered = original
    tampered[tampered.count - 1] ^= 1
    #expect(throws: NoiseFailure.authenticationFailed) { try pair.initiator.readMessage(tampered) }
    #expect(throws: NoiseFailure.failed) { try pair.initiator.readMessage(original) }
    #expect(throws: NoiseFailure.failed) { try pair.initiator.writeMessage() }
}

@Test func noiseHandshakeBindsThePrologueIncludingRoutingIDsAndChannelID() throws {
    let first = NoiseKeyPair()
    let second = NoiseKeyPair()
    let one = try RelayWire.prologue(initiator: first.deviceID, responder: second.deviceID, channelID: Data(repeating: 1, count: 8))
    let other = try RelayWire.prologue(initiator: first.deviceID, responder: second.deviceID, channelID: Data(repeating: 2, count: 8))
    var initiator = try NoiseXXHandshake(role: .initiator, staticKey: first, prologue: one, expectedPeerID: second.deviceID)
    var responder = try NoiseXXHandshake(role: .responder, staticKey: second, prologue: other, expectedPeerID: first.deviceID)
    _ = try responder.readMessage(initiator.writeMessage())
    let response = try responder.writeMessage()
    #expect(throws: NoiseFailure.authenticationFailed) { try initiator.readMessage(response) }
}

@Test func noiseTransportAuthenticationFailureDisablesBothDirections() throws {
    var pair = try NoisePair()
    try pair.complete()
    let message = try pair.initiator.encrypt(Data("private session".utf8))
    var tampered = message
    tampered[0] ^= 1
    #expect(throws: NoiseFailure.authenticationFailed) { try pair.responder.decrypt(tampered) }
    #expect(throws: NoiseFailure.failed) { try pair.responder.decrypt(message) }
    #expect(throws: NoiseFailure.failed) { try pair.responder.encrypt(Data()) }
    #expect(!pair.responder.isComplete)
}

@Test func noiseTransportRejectsReplayAndOutOfOrderMessages() throws {
    var pair = try NoisePair()
    try pair.complete()
    let first = try pair.initiator.encrypt(Data([1]))
    #expect(try pair.responder.decrypt(first) == Data([1]))
    #expect(throws: NoiseFailure.authenticationFailed) { try pair.responder.decrypt(first) }
    var another = try NoisePair()
    try another.complete()
    _ = try another.initiator.encrypt(Data([1]))
    let second = try another.initiator.encrypt(Data([2]))
    #expect(throws: NoiseFailure.authenticationFailed) { try another.responder.decrypt(second) }
}

@Test func noiseEnforcesHandshakeAndTransportLimitsWithoutSpendingNonceOnOversizedSends() throws {
    var pair = try NoisePair()
    #expect(throws: NoiseFailure.messageTooLarge) { try pair.initiator.writeMessage(Data(repeating: 0, count: 65471)) }
    let firstPayload = Data(repeating: 7, count: 65470)
    let first = try pair.initiator.writeMessage(firstPayload)
    #expect(first.count == NoiseXXHandshake.maximumMessageSize)
    #expect(try pair.responder.readMessage(first) == firstPayload)
    #expect(throws: NoiseFailure.messageTooLarge) { try pair.responder.writeMessage(Data(repeating: 0, count: 65374)) }
    let secondPayload = Data(repeating: 8, count: 65373)
    let second = try pair.responder.writeMessage(secondPayload)
    #expect(second.count == NoiseXXHandshake.maximumMessageSize)
    #expect(try pair.initiator.readMessage(second) == secondPayload)
    #expect(throws: NoiseFailure.messageTooLarge) { try pair.initiator.writeMessage(Data(repeating: 0, count: 65439)) }
    let thirdPayload = Data(repeating: 9, count: 65438)
    let third = try pair.initiator.writeMessage(thirdPayload)
    #expect(third.count == NoiseXXHandshake.maximumMessageSize)
    #expect(try pair.responder.readMessage(third) == thirdPayload)
    #expect(throws: NoiseFailure.messageTooLarge) { try pair.initiator.encrypt(Data(repeating: 0, count: 65520)) }
    let large = Data(repeating: 42, count: 65519)
    let cipher = try pair.initiator.encrypt(large)
    #expect(cipher.count == NoiseXXHandshake.maximumMessageSize)
    #expect(try pair.responder.decrypt(cipher) == large)
    #expect(try pair.responder.decrypt(pair.initiator.encrypt(Data())) == Data())
}

@Test func noiseOversizedAndTruncatedReceivedMessagesPoisonTheChannel() throws {
    var pair = try NoisePair()
    #expect(throws: NoiseFailure.messageTooLarge) { try pair.responder.readMessage(Data(repeating: 0, count: 65536)) }
    #expect(throws: NoiseFailure.failed) { try pair.responder.readMessage(Data()) }
    var another = try NoisePair()
    #expect(throws: NoiseFailure.invalidMessage) { try another.responder.readMessage(Data([4])) }
    #expect(throws: NoiseFailure.failed) { try another.responder.readMessage(Data()) }
    var completed = try NoisePair()
    try completed.complete()
    #expect(throws: NoiseFailure.invalidMessage) { try completed.responder.decrypt(Data(repeating: 0, count: 15)) }
    #expect(throws: NoiseFailure.failed) { try completed.responder.decrypt(Data()) }
}
