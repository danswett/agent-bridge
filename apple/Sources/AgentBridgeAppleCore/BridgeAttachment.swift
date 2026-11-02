import CryptoKit
import Foundation

/// Chunk preparation is independent of transport. Resume only from the host's returned
/// offset, with the same attachment identity; do not infer receipt from a sent record.
public struct BridgeAttachment: Identifiable, Sendable {
    public static let maximumBytes = 256 * 1024
    public static let maximumDirectBytes = 100 * 1024 * 1024
    public static let chunkBytes = 11 * 1024
    public let id: UUID
    public let name: String
    public let type: String?
    public let bytes: Data
    public let sha256: String

    public init(name: String, type: String? = nil, bytes: Data, capabilities: Set<String>, isLocal: Bool, id: UUID = UUID()) throws {
        try BridgeOperationValidation.require("sessions.attach", in: capabilities)
        try BridgeOperationValidation.require("sessions.reply", in: capabilities)
        let maximum = isLocal && capabilities.contains("sessions.attach.direct") ? Self.maximumDirectBytes : Self.maximumBytes
        guard !bytes.isEmpty, bytes.count <= maximum else { throw BridgeOperationError("The file must contain 1 to \(maximum) bytes for this connection.") }
        guard (1 ... 255).contains(name.utf8.count) else { throw BridgeOperationError("The attachment name must contain 1 to 255 UTF-8 bytes.") }
        if let type, type.utf8.count > 127 {
            throw BridgeOperationError("The attachment type is longer than 127 UTF-8 bytes.")
        }
        self.id = id
        self.name = name
        self.type = type
        self.bytes = Data(bytes)
        sha256 = Data(SHA256.hash(data: bytes)).base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }

    public var start: BridgeOperation {
        var data: [String: JSONValue] = ["attachment": .string(id.uuidString.lowercased()), "name": .string(name), "size": .number(Double(bytes.count)), "sha256": .string(sha256)]
        if let type {
            data["type"] = .string(type)
        }
        return BridgeOperation(op: "attachment.start", data: data)
    }

    public var discard: BridgeOperation {
        BridgeOperation(op: "attachment.discard", data: ["attachment": .string(id.uuidString.lowercased())])
    }

    public func chunk(after state: BridgeAttachmentState) throws -> BridgeOperation? {
        guard state.received <= bytes.count, !state.complete || state.received == bytes.count else {
            throw BridgeOperationError("The machine reported an invalid attachment offset.")
        }
        if state.complete {
            return nil
        }
        guard state.received < bytes.count else { throw BridgeOperationError("The machine has all bytes but has not confirmed the file.") }
        func operation(count: Int) -> BridgeOperation {
            let end = min(bytes.count, state.received + count)
            return BridgeOperation(op: "attachment.chunk", data: ["attachment": .string(id.uuidString.lowercased()), "offset": .number(Double(state.received)), "data": .string(bytes.subdata(in: state.received ..< end).base64EncodedString())])
        }
        let candidate = operation(count: Self.chunkBytes)
        // JSONEncoder escapes slashes by default. Base64 full of '/' can otherwise
        // double the record size even though its decoded chunk fits the protocol limit.
        if try JSONEncoder().encode(candidate.request(id: Int.max)).count > 16 * 1024 {
            return operation(count: Self.chunkBytes / 2)
        }
        return candidate
    }
}

public struct BridgeAttachmentState: Sendable, Equatable {
    public let received: Int
    public let complete: Bool

    public init(json: JSONValue) throws {
        guard let received = json["received"]?.number, received.isFinite, received >= 0,
              received <= Double(BridgeAttachment.maximumDirectBytes), received.rounded() == received,
              let complete = json["complete"]?.bool else { throw BridgeOperationError("The machine's attachment response is invalid.") }
        self.received = Int(received)
        self.complete = complete
    }
}
