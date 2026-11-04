import CryptoKit
import Foundation

/// Bridge's Noise XX variant, interoperable with its .NET and WebCrypto peers.
/// One connection actor must own this state, call it in wire order, and never copy
/// an active instance: copying a cipher state would reuse a transport nonce.
public struct NoiseXXHandshake: Sendable {
    public enum Role: Sendable { case initiator, responder }

    public static let protocolName = "Noise_XX_P256_AESGCM_SHA256"
    public static let maximumMessageSize = 65535
    public static let maximumPlaintextSize = 65519

    private let role: Role
    private let staticKey: NoiseKeyPair
    private let expectedPeerID: String
    private var ephemeralKey: NoiseKeyPair?
    private var remoteEphemeral: Data?
    private var symmetric = NoiseSymmetricState()
    private var sendCipher: NoiseCipherState?
    private var receiveCipher: NoiseCipherState?
    private var step = 0
    private var failed = false

    public private(set) var remoteStaticKey: Data?
    public var isComplete: Bool {
        step == 3 && !failed
    }

    public var handshakeHash: Data {
        symmetric.hash
    }

    public init(role: Role, staticKey: NoiseKeyPair, prologue: Data, expectedPeerID: String) throws {
        try self.init(role: role, staticKey: staticKey, prologue: prologue,
                      expectedPeerID: expectedPeerID, fixedEphemeral: nil)
    }

    /// Deterministic ephemerals are for contract vectors only. Production callers
    /// never get a knob that could reuse an ephemeral key across handshakes.
    init(role: Role, staticKey: NoiseKeyPair, prologue: Data, expectedPeerID: String,
         fixedEphemeral: NoiseKeyPair?) throws
    {
        guard RelayWire.isDeviceID(expectedPeerID) else { throw NoiseFailure.invalidMessage }
        self.role = role
        self.staticKey = staticKey
        self.expectedPeerID = expectedPeerID.lowercased()
        ephemeralKey = fixedEphemeral
        symmetric.mixHash(prologue)
    }

    private var isWriteTurn: Bool {
        role == .initiator ? step.isMultiple(of: 2) : !step.isMultiple(of: 2)
    }

    public mutating func writeMessage(_ payload: Data = Data()) throws -> Data {
        guard !failed else { throw NoiseFailure.failed }
        guard step < 3, isWriteTurn else { throw NoiseFailure.invalidState }
        // Check before touching the hash or nonce so a rejected oversized local
        // payload does not desynchronize an otherwise usable handshake.
        let overhead = [65, 162, 97][step]
        guard payload.count <= Self.maximumMessageSize - overhead else { throw NoiseFailure.messageTooLarge }
        do {
            var message = Data()
            switch step {
            case 0:
                let ephemeral = ephemeralKey ?? NoiseKeyPair()
                ephemeralKey = ephemeral
                message.append(ephemeral.publicKey)
                symmetric.mixHash(ephemeral.publicKey)
            case 1:
                guard let remoteEphemeral else { throw NoiseFailure.invalidState }
                let ephemeral = ephemeralKey ?? NoiseKeyPair()
                ephemeralKey = ephemeral
                message.append(ephemeral.publicKey)
                symmetric.mixHash(ephemeral.publicKey)
                try symmetric.mixKey(ephemeral.sharedSecret(with: remoteEphemeral))
                try message.append(symmetric.encryptAndHash(staticKey.publicKey))
                try symmetric.mixKey(staticKey.sharedSecret(with: remoteEphemeral))
            case 2:
                guard let remoteEphemeral else { throw NoiseFailure.invalidState }
                try message.append(symmetric.encryptAndHash(staticKey.publicKey))
                try symmetric.mixKey(staticKey.sharedSecret(with: remoteEphemeral))
            default:
                throw NoiseFailure.invalidState
            }
            try message.append(symmetric.encryptAndHash(payload))
            advance()
            return message
        } catch {
            poison()
            throw error
        }
    }

    public mutating func readMessage(_ message: Data) throws -> Data {
        guard !failed else { throw NoiseFailure.failed }
        guard step < 3, !isWriteTurn else { throw NoiseFailure.invalidState }
        do {
            guard message.count <= Self.maximumMessageSize else { throw NoiseFailure.messageTooLarge }
            var reader = NoiseMessageReader(message)
            switch step {
            case 0:
                let remote = try reader.take(65)
                try NoiseKeyPair.validatePublicKey(remote)
                remoteEphemeral = remote
                symmetric.mixHash(remote)
            case 1:
                guard let ephemeralKey else { throw NoiseFailure.invalidState }
                let remote = try reader.take(65)
                try NoiseKeyPair.validatePublicKey(remote)
                remoteEphemeral = remote
                symmetric.mixHash(remote)
                try symmetric.mixKey(ephemeralKey.sharedSecret(with: remote))
                let peer = try symmetric.decryptAndHash(reader.take(81))
                try bindPeer(peer)
                try symmetric.mixKey(ephemeralKey.sharedSecret(with: peer))
            case 2:
                guard let ephemeralKey else { throw NoiseFailure.invalidState }
                let peer = try symmetric.decryptAndHash(reader.take(81))
                try bindPeer(peer)
                try symmetric.mixKey(ephemeralKey.sharedSecret(with: peer))
            default:
                throw NoiseFailure.invalidState
            }
            let payload = try symmetric.decryptAndHash(reader.remainder)
            advance()
            return payload
        } catch {
            poison()
            throw error
        }
    }

    public mutating func encrypt(_ plaintext: Data) throws -> Data {
        guard !failed else { throw NoiseFailure.failed }
        guard isComplete, sendCipher != nil else { throw NoiseFailure.invalidState }
        guard plaintext.count <= Self.maximumPlaintextSize else { throw NoiseFailure.messageTooLarge }
        do { return try sendCipher!.encrypt(plaintext, authenticating: Data()) }
        catch { poison(); throw error }
    }

    public mutating func decrypt(_ ciphertext: Data) throws -> Data {
        guard !failed else { throw NoiseFailure.failed }
        guard isComplete, receiveCipher != nil else { throw NoiseFailure.invalidState }
        do {
            guard ciphertext.count <= Self.maximumMessageSize else { throw NoiseFailure.messageTooLarge }
            return try receiveCipher!.decrypt(ciphertext, authenticating: Data())
        } catch {
            // Unlike the primitive cipher in .NET, the client channel must never
            // continue after authentication fails: ordered Noise cannot resync.
            poison()
            throw error
        }
    }

    private mutating func bindPeer(_ peer: Data) throws {
        guard try RelayWire.deviceID(publicKey: peer) == expectedPeerID else { throw NoiseFailure.peerMismatch }
        remoteStaticKey = peer
    }

    private mutating func advance() {
        step += 1
        if step == 3 {
            let (first, second) = symmetric.split()
            (sendCipher, receiveCipher) = role == .initiator ? (first, second) : (second, first)
            ephemeralKey = nil
            remoteEphemeral = nil
        }
    }

    private mutating func poison() {
        failed = true
        sendCipher = nil
        receiveCipher = nil
        ephemeralKey = nil
        remoteEphemeral = nil
        remoteStaticKey = nil
        symmetric = NoiseSymmetricState()
    }
}

private struct NoiseMessageReader {
    private let bytes: [UInt8]
    private var offset = 0

    init(_ data: Data) {
        bytes = Array(data)
    }

    mutating func take(_ count: Int) throws -> Data {
        guard count <= bytes.count - offset else { throw NoiseFailure.invalidMessage }
        defer { offset += count }
        return Data(bytes[offset ..< offset + count])
    }

    var remainder: Data {
        Data(bytes.dropFirst(offset))
    }
}

private struct NoiseSymmetricState: Sendable {
    private var chainingKey: Data
    private var cipher = NoiseCipherState()
    private(set) var hash: Data

    init() {
        let name = Data(NoiseXXHandshake.protocolName.utf8)
        hash = name.count <= 32 ? name + Data(repeating: 0, count: 32 - name.count) : Data(SHA256.hash(data: name))
        chainingKey = hash
    }

    mutating func mixHash(_ data: Data) {
        hash = Data(SHA256.hash(data: hash + data))
    }

    mutating func mixKey(_ input: Data) {
        let (next, key) = hkdf(input)
        chainingKey = next
        cipher = NoiseCipherState(key: key)
    }

    mutating func encryptAndHash(_ plaintext: Data) throws -> Data {
        let ciphertext = try cipher.encrypt(plaintext, authenticating: hash)
        mixHash(ciphertext)
        return ciphertext
    }

    mutating func decryptAndHash(_ ciphertext: Data) throws -> Data {
        let plaintext = try cipher.decrypt(ciphertext, authenticating: hash)
        mixHash(ciphertext)
        return plaintext
    }

    func split() -> (NoiseCipherState, NoiseCipherState) {
        let (first, second) = hkdf(Data())
        return (NoiseCipherState(key: first), NoiseCipherState(key: second))
    }

    private func hkdf(_ input: Data) -> (Data, Data) {
        let temporary = SymmetricKey(data: HMAC<SHA256>.authenticationCode(for: input, using: SymmetricKey(data: chainingKey)))
        let first = Data(HMAC<SHA256>.authenticationCode(for: Data([1]), using: temporary))
        let second = Data(HMAC<SHA256>.authenticationCode(for: first + Data([2]), using: temporary))
        return (first, second)
    }
}

private struct NoiseCipherState: Sendable {
    private var key: SymmetricKey?
    private var counter: UInt64 = 0

    init(key: Data? = nil) {
        self.key = key.map { SymmetricKey(data: $0) }
    }

    private func nonce() throws -> AES.GCM.Nonce {
        guard counter < UInt64.max else { throw NoiseFailure.nonceExhausted }
        var bigEndian = counter.bigEndian
        let bytes = Data(repeating: 0, count: 4) + withUnsafeBytes(of: &bigEndian) { Data($0) }
        return try AES.GCM.Nonce(data: bytes)
    }

    mutating func encrypt(_ plaintext: Data, authenticating data: Data) throws -> Data {
        guard let key else { return plaintext }
        let sealed = try AES.GCM.seal(plaintext, using: key, nonce: nonce(), authenticating: data)
        counter += 1
        // CryptoKit's ciphertext can be a Data slice whose startIndex is 12.
        // Build the wire message from a fresh buffer with ordinary zero indexing.
        var ciphertext = Data(capacity: plaintext.count + 16)
        ciphertext.append(sealed.ciphertext)
        ciphertext.append(sealed.tag)
        return ciphertext
    }

    mutating func decrypt(_ ciphertext: Data, authenticating data: Data) throws -> Data {
        guard let key else { return ciphertext }
        guard ciphertext.count >= 16 else { throw NoiseFailure.invalidMessage }
        let nonce = try nonce()
        do {
            let sealed = try AES.GCM.SealedBox(nonce: nonce, ciphertext: ciphertext.dropLast(16), tag: ciphertext.suffix(16))
            let plaintext = try AES.GCM.open(sealed, using: key, authenticating: data)
            counter += 1
            return plaintext
        } catch {
            throw NoiseFailure.authenticationFailed
        }
    }
}
