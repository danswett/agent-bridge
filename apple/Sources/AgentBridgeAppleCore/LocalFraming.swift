import Foundation

/// The host's local socket uses a little-endian UInt32 length followed by UTF-8 JSON.
/// A local frame must not exceed 65,519 bytes (docs/protocol/local-pipe.md).
public enum LocalFraming {
    public static let maximumPayloadSize = 65_519

    public enum FrameError: Error, Equatable, Sendable {
        case oversized(Int)
        case truncated
    }

    public static func encode(_ payload: Data) throws -> Data {
        guard payload.count <= maximumPayloadSize else { throw FrameError.oversized(payload.count) }
        var length = UInt32(payload.count).littleEndian
        var frame = withUnsafeBytes(of: &length) { Data($0) }
        frame.append(payload)
        return frame
    }

    /// Decodes exactly one complete frame. For streaming sockets, buffer until the
    /// header and its declared payload have arrived before calling this method.
    public static func decode(_ frame: Data) throws -> Data {
        guard frame.count >= 4 else { throw FrameError.truncated }
        let length = frame.prefix(4).enumerated().reduce(UInt32(0)) { value, item in
            value | (UInt32(item.element) << (UInt32(item.offset) * 8))
        }
        guard length <= maximumPayloadSize else { throw FrameError.oversized(Int(length)) }
        guard frame.count == Int(length) + 4 else { throw FrameError.truncated }
        return frame.dropFirst(4)
    }
}
