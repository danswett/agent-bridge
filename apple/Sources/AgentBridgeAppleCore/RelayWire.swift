import CryptoKit
import Foundation

/// The relay sees these routing headers, never the Noise-encrypted session JSON.
public enum RelayWire {
    public static let maximumFrameSize = 65561
    public static let maximumControlSize = 4096

    public enum Kind: UInt8, Sendable { case msg1 = 1, msg2, msg3, transport, close }
    public enum Failure: Error, Equatable, Sendable { case invalidHex, invalidDeviceID, invalidChannelID, invalidFrame, invalidProof }

    public struct Frame: Equatable, Sendable {
        public let peerID: String
        public let kind: Kind
        public let channelID: Data
        public let body: Data
    }

    public static func hex(_ data: Data) -> String {
        let digits = Array("0123456789abcdef".utf8)
        return String(decoding: data.flatMap { [digits[Int($0 >> 4)], digits[Int($0 & 15)]] }, as: UTF8.self)
    }

    public static func unhex(_ string: String) throws -> Data {
        let bytes = Array(string.utf8)
        guard bytes.count.isMultiple(of: 2) else { throw Failure.invalidHex }
        func nibble(_ byte: UInt8) throws -> UInt8 {
            switch byte {
            case 48 ... 57: byte - 48
            case 65 ... 70: byte - 55
            case 97 ... 102: byte - 87
            default: throw Failure.invalidHex
            }
        }
        var result = Data(capacity: bytes.count / 2)
        for index in stride(from: 0, to: bytes.count, by: 2) {
            try result.append(nibble(bytes[index]) << 4 | nibble(bytes[index + 1]))
        }
        return result
    }

    public static func isDeviceID(_ value: String) -> Bool {
        value.utf8.count == 32 && (try? unhex(value)) != nil
    }

    public static func deviceID(publicKey: Data) throws -> String {
        try NoiseKeyPair.validatePublicKey(publicKey)
        return hex(Data(SHA256.hash(data: publicKey).prefix(16)))
    }

    public static func proofMAC(sharedSecret: Data, nonce: Data, accountID: String) throws -> Data {
        guard sharedSecret.count == 32, nonce.count == 32, !accountID.isEmpty,
              accountID.utf8.count <= maximumControlSize else { throw Failure.invalidProof }
        let bytes = Data("agent-bridge-s4-relay-proof".utf8) + nonce + Data(accountID.utf8)
        return Data(HMAC<SHA256>.authenticationCode(for: bytes, using: SymmetricKey(data: sharedSecret)))
    }

    public static func prologue(initiator: String, responder: String, channelID: Data) throws -> Data {
        guard isDeviceID(initiator), isDeviceID(responder) else { throw Failure.invalidDeviceID }
        guard channelID.count == 8 else { throw Failure.invalidChannelID }
        return Data("agent-bridge-s4|\(initiator.lowercased())|\(responder.lowercased())|".utf8) + channelID
    }

    public static func frame(peerID: String, kind: Kind, channelID: Data, body: Data) throws -> Data {
        guard isDeviceID(peerID) else { throw Failure.invalidDeviceID }
        guard channelID.count == 8 else { throw Failure.invalidChannelID }
        guard body.count <= NoiseXXHandshake.maximumMessageSize,
              kind != .close || body.isEmpty else { throw Failure.invalidFrame }
        return try Data([1]) + unhex(peerID) + Data([kind.rawValue]) + channelID + body
    }

    public static func parseFrame(_ data: Data) throws -> Frame {
        guard (26 ... maximumFrameSize).contains(data.count) else { throw Failure.invalidFrame }
        let bytes = Array(data)
        guard bytes[0] == 1, let kind = Kind(rawValue: bytes[17]),
              kind != .close || bytes.count == 26 else { throw Failure.invalidFrame }
        return Frame(peerID: hex(Data(bytes[1 ..< 17])), kind: kind,
                     channelID: Data(bytes[18 ..< 26]), body: Data(bytes.dropFirst(26)))
    }
}
