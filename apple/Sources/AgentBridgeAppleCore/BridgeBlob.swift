import CryptoKit
import Foundation

/// Blob format v1, shared with AgentBridge.Transport/Blobs.cs and the PWA.
public enum BridgeBlob {
    public static let recordBytes = 64 * 1024
    public static let maximumPlaintextBytes = 200 * 1024 * 1024
    public static let maximumAttachmentBytes = 100 * 1024 * 1024
    public static let minimumStoredBytes = 4096
    private static let label = Data("agent-bridge blob v1".utf8)

    public static func sealedSize(_ size: Int) throws -> Int {
        try validateSize(size)
        return size + ((size + recordBytes - 1) / recordBytes) * 16
    }

    public static func storedSize(_ size: Int) throws -> Int {
        try bucket(sealedSize(size))
    }

    public static func bucket(_ size: Int) throws -> Int {
        guard size >= 0, size <= 201 * 1024 * 1024 else { throw BridgeOperationError("The blob size is outside the supported range.") }
        if size <= minimumStoredBytes {
            return minimumStoredBytes
        }
        if size <= 1024 * 1024 {
            var bucket = minimumStoredBytes
            while bucket < size {
                bucket *= 2
            }
            return bucket
        }
        return ((size + 1024 * 1024 - 1) / (1024 * 1024)) * 1024 * 1024
    }

    public static func maximumPlaintext(storedLimit: Int) throws -> Int {
        guard storedLimit >= minimumStoredBytes else { return 0 }
        var lower = 0
        var upper = min(storedLimit, maximumPlaintextBytes)
        while lower < upper {
            let middle = lower + (upper - lower + 1) / 2
            if try storedSize(middle) <= storedLimit {
                lower = middle
            } else {
                upper = middle - 1
            }
        }
        return lower
    }

    public static func newKey() -> Data {
        SymmetricKey(size: .bits256).withUnsafeBytes { Data($0) }
    }

    public static func putMessage(id: Int, recipient: String, plaintextSize: Int, storedLimit: Int) throws -> JSONValue {
        guard id > 0, id <= 9_007_199_254_740_991, BridgeSignedCommand.validDeviceID(recipient) else { throw BridgeOperationError("The blob request identity is invalid.") }
        let size = try storedSize(plaintextSize)
        guard size <= storedLimit else { throw BridgeOperationError("The file exceeds the relay's blob limit.") }
        return .object(["t": .string("blob-put"), "id": .number(Double(id)), "to": .string(recipient), "size": .number(Double(size))])
    }

    public static func getMessage(id: Int, ticket: String) throws -> JSONValue {
        try ticketMessage(type: "blob-get", id: id, ticket: ticket)
    }

    public static func deleteMessage(id: Int, ticket: String) throws -> JSONValue {
        try ticketMessage(type: "blob-delete", id: id, ticket: ticket)
    }

    private static func ticketMessage(type: String, id: Int, ticket: String) throws -> JSONValue {
        guard id > 0, id <= 9_007_199_254_740_991 else { throw BridgeOperationError("The blob request identity is invalid.") }
        _ = try ticketID(ticket)
        return .object(["t": .string(type), "id": .number(Double(id)), "blob": .string(ticket)])
    }

    public static func ticketID(_ ticket: String) throws -> Data {
        let parts = ticket.split(separator: ".", omittingEmptySubsequences: false)
        guard ticket.utf8.count <= 512, parts.count == 3, parts[0].count == 22,
              let id = BridgeBase64URL.decode(String(parts[0])), id.count == 16,
              BridgeBase64URL.decode(String(parts[1])) != nil, BridgeBase64URL.decode(String(parts[2])) != nil
        else {
            throw BridgeOperationError("The relay returned an invalid blob ticket.")
        }
        return id
    }

    public static func seal(_ plaintext: Data, key: Data, id: Data) throws -> Data {
        try validate(key: key, id: id)
        try validateSize(plaintext.count)
        let bytes = Data(plaintext)
        var stored = try Data(capacity: storedSize(bytes.count))
        var offset = 0
        var index = 0
        while offset < bytes.count {
            let end = min(offset + recordBytes, bytes.count)
            try stored.append(sealRecord(bytes.subdata(in: offset ..< end), key: key, id: id, index: index, final: end == bytes.count))
            offset = end
            index += 1
        }
        try stored.append(Data(count: storedSize(bytes.count) - stored.count))
        return stored
    }

    public static func open(_ stored: Data, plaintextSize: Int, key: Data, id: Data, expectedSHA256: Data) throws -> Data {
        try validate(key: key, id: id)
        guard expectedSHA256.count == 32, try stored.count == storedSize(plaintextSize) else { throw BridgeOperationError("The stored blob has the wrong size or digest.") }
        let stored = Data(stored)
        var plaintext = Data(capacity: plaintextSize)
        var offset = 0
        var index = 0
        while plaintext.count < plaintextSize {
            let count = min(recordBytes, plaintextSize - plaintext.count)
            let sealed = stored.subdata(in: offset ..< (offset + count + 16))
            try plaintext.append(openRecord(sealed, key: key, id: id, index: index, final: plaintext.count + count == plaintextSize))
            offset += count + 16
            index += 1
        }
        guard stored[offset...].allSatisfy({ $0 == 0 }), Data(SHA256.hash(data: plaintext)) == expectedSHA256 else {
            throw BridgeOperationError("The blob's padding or file digest did not match.")
        }
        return plaintext
    }

    /// Use a private temporary output file and discard it if this throws. This bounds
    /// memory to one record even for a 200 MiB transferred session.
    public static func seal(input: FileHandle, plaintextSize: Int, output: FileHandle, key: Data, id: Data) throws {
        try validate(key: key, id: id)
        try validateSize(plaintextSize)
        var remaining = plaintextSize
        var index = 0
        while remaining > 0 {
            try Task.checkCancellation()
            let count = min(recordBytes, remaining)
            let plaintext = try readExactly(input, count: count)
            try output.write(contentsOf: sealRecord(plaintext, key: key, id: id, index: index, final: count == remaining))
            remaining -= count
            index += 1
        }
        guard try input.read(upToCount: 1)?.isEmpty != false else { throw BridgeOperationError("The file changed size while it was being encrypted.") }
        var padding = try storedSize(plaintextSize) - sealedSize(plaintextSize)
        while padding > 0 {
            try Task.checkCancellation()
            let count = min(recordBytes, padding)
            try output.write(contentsOf: Data(count: count))
            padding -= count
        }
    }

    /// The output remains provisional until every record, padding byte and the complete
    /// file's SHA-256 have passed. A caller must remove it after any failure.
    public static func open(input: FileHandle, plaintextSize: Int, output: FileHandle, key: Data, id: Data, expectedSHA256: Data) throws {
        try validate(key: key, id: id)
        try validateSize(plaintextSize)
        guard expectedSHA256.count == 32 else { throw BridgeOperationError("The blob's file digest is invalid.") }
        var remaining = plaintextSize
        var index = 0
        var hash = SHA256()
        while remaining > 0 {
            try Task.checkCancellation()
            let count = min(recordBytes, remaining)
            let sealed = try readExactly(input, count: count + 16)
            let plaintext = try openRecord(sealed, key: key, id: id, index: index, final: count == remaining)
            hash.update(data: plaintext)
            try output.write(contentsOf: plaintext)
            remaining -= count
            index += 1
        }
        var padding = try storedSize(plaintextSize) - sealedSize(plaintextSize)
        while padding > 0 {
            let count = min(recordBytes, padding)
            guard try readExactly(input, count: count).allSatisfy({ $0 == 0 }) else { throw BridgeOperationError("The blob has nonzero padding.") }
            padding -= count
        }
        guard try input.read(upToCount: 1)?.isEmpty != false, Data(hash.finalize()) == expectedSHA256 else { throw BridgeOperationError("The blob's length or file digest did not match.") }
    }

    /// Validate every URL before using it, and configure the URLSession delegate to
    /// refuse redirects. A permitted first URL cannot authorize a redirected origin.
    public static func resolveURL(_ string: String, relay: URL) throws -> URL {
        guard let url = URL(string: string, relativeTo: relay)?.absoluteURL,
              let host = url.host?.lowercased(), url.user == nil, url.password == nil,
              url.fragment == nil, let scheme = url.scheme?.lowercased() else { throw BridgeOperationError("The blob URL is invalid.") }
        let port = url.port ?? (scheme == "https" ? 443 : 80)
        let relayScheme = relay.scheme?.lowercased()
        let relayPort = relay.port ?? (relayScheme == "https" ? 443 : 80)
        let sameOrigin = (scheme == "http" || scheme == "https") && scheme == relayScheme && host == relay.host?.lowercased() && port == relayPort
        let azure = scheme == "https" && port == 443 && host.hasSuffix(".blob.core.windows.net") && host.count > ".blob.core.windows.net".count
        guard sameOrigin || azure else { throw BridgeOperationError("The blob URL is outside the relay or Azure Blob Storage.") }
        return url
    }

    private static func validateSize(_ size: Int) throws {
        guard size > 0, size <= maximumPlaintextBytes else { throw BridgeOperationError("A blob must contain 1 to 200 MiB of plaintext.") }
    }

    private static func validate(key: Data, id: Data) throws {
        guard key.count == 32, id.count == 16 else { throw BridgeOperationError("A blob requires a 32-byte key and a 16-byte ID.") }
    }

    private static func nonce(_ index: Int) throws -> AES.GCM.Nonce {
        try AES.GCM.Nonce(data: Data(repeating: 0, count: 4) + bigEndian(index))
    }

    private static func aad(id: Data, index: Int, final: Bool) -> Data {
        label + id + bigEndian(index) + Data([final ? 1 : 0])
    }

    private static func bigEndian(_ value: Int) -> Data {
        withUnsafeBytes(of: UInt64(value).bigEndian) { Data($0) }
    }

    private static func sealRecord(_ plaintext: Data, key: Data, id: Data, index: Int, final: Bool) throws -> Data {
        let sealed = try AES.GCM.seal(plaintext, using: SymmetricKey(data: key), nonce: nonce(index), authenticating: aad(id: id, index: index, final: final))
        return sealed.ciphertext + sealed.tag
    }

    private static func openRecord(_ sealed: Data, key: Data, id: Data, index: Int, final: Bool) throws -> Data {
        let box = try AES.GCM.SealedBox(nonce: nonce(index), ciphertext: sealed.dropLast(16), tag: sealed.suffix(16))
        return try AES.GCM.open(box, using: SymmetricKey(data: key), authenticating: aad(id: id, index: index, final: final))
    }

    private static func readExactly(_ input: FileHandle, count: Int) throws -> Data {
        var bytes = Data(capacity: count)
        while bytes.count < count {
            guard let part = try input.read(upToCount: count - bytes.count), !part.isEmpty else { throw BridgeOperationError("The blob or file ended before its declared size.") }
            bytes.append(part)
        }
        return bytes
    }
}

public struct BridgeBlobAttachment: Sendable {
    public let id: UUID
    public let name: String
    public let type: String?
    public let size: Int
    public let sha256: Data

    public init(name: String, type: String? = nil, size: Int, sha256: Data, id: UUID = UUID()) throws {
        guard (1 ... 255).contains(name.utf8.count), (type?.utf8.count ?? 0) <= 127,
              size > 256 * 1024, size <= BridgeBlob.maximumAttachmentBytes, sha256.count == 32
        else {
            throw BridgeOperationError("The blob attachment metadata is invalid; use channel chunks for files up to 256 KiB.")
        }
        self.id = id
        self.name = name
        self.type = type
        self.size = size
        self.sha256 = sha256
    }

    public func start(ticket: String, key: Data, capabilities: Set<String>, isLocal: Bool) throws -> BridgeOperation {
        try BridgeOperationValidation.require("sessions.reply", in: capabilities)
        try BridgeOperationValidation.require("sessions.attach", in: capabilities)
        try BridgeOperationValidation.require("sessions.attach.blob", in: capabilities)
        guard !isLocal, key.count == 32 else { throw BridgeOperationError("Blob attachments require a relay connection and a 32-byte key.") }
        _ = try BridgeBlob.ticketID(ticket)
        var data: [String: JSONValue] = ["attachment": .string(id.uuidString.lowercased()), "name": .string(name), "size": .number(Double(size)),
                                         "sha256": .string(BridgeBase64URL.encode(sha256)), "blob": .object(["id": .string(ticket), "key": .string(BridgeBase64URL.encode(key))])]
        if let type {
            data["type"] = .string(type)
        }
        return BridgeOperation(op: "attachment.start", data: data)
    }
}
