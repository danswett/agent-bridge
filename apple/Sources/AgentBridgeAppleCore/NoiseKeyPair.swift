import CryptoKit
import Foundation

public enum NoiseFailure: Error, Equatable, Sendable {
    case invalidPublicKey
    case invalidMessage
    case invalidState
    case messageTooLarge
    case authenticationFailed
    case peerMismatch
    case nonceExhausted
    case failed
}

/// A device's P-256 agreement key. Persist the raw private representation only in
/// the Keychain; public keys on the Bridge wire are uncompressed SEC1 points.
public struct NoiseKeyPair: Sendable {
    private let key: P256.KeyAgreement.PrivateKey

    public init() {
        key = P256.KeyAgreement.PrivateKey(compactRepresentable: false)
    }

    public init(rawRepresentation: Data) throws {
        key = try P256.KeyAgreement.PrivateKey(rawRepresentation: rawRepresentation)
    }

    public var rawRepresentation: Data {
        key.rawRepresentation
    }

    public var publicKey: Data {
        key.publicKey.x963Representation
    }

    public var deviceID: String {
        RelayWire.hex(Data(SHA256.hash(data: publicKey).prefix(16)))
    }

    public func sharedSecret(with peerPublicKey: Data) throws -> Data {
        let peer = try Self.validatePublicKey(peerPublicKey)
        return try key.sharedSecretFromKeyAgreement(with: peer).withUnsafeBytes { Data($0) }
    }

    @discardableResult
    static func validatePublicKey(_ bytes: Data) throws -> P256.KeyAgreement.PublicKey {
        guard bytes.count == 65, bytes.first == 4 else { throw NoiseFailure.invalidPublicKey }
        do {
            // CryptoKit's point import validates the coordinates before ECDH. Tests
            // cover malformed, off-curve and out-of-field public keys explicitly.
            return try P256.KeyAgreement.PublicKey(x963Representation: bytes)
        } catch {
            throw NoiseFailure.invalidPublicKey
        }
    }
}
