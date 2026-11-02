import Foundation
import AgentBridgeAppleCore
#if os(macOS)
import Darwin
#endif

/// Local host transport only. Remote clients must use authenticated Noise channels.
actor LocalBridge {
    enum Failure: Error { case unsupportedPlatform, socket(String), closed, oversizedFrame, unexpectedResponse }
    #if os(macOS)
    private var fd: Int32 = -1
    #endif

    func connect(path: String) throws {
        #if os(macOS)
        if fd >= 0 { Darwin.close(fd) }
        fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { throw Failure.socket("socket") }
        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        let bytes = Array(path.utf8CString)
        guard bytes.count <= MemoryLayout.size(ofValue: address.sun_path) else {
            Darwin.close(fd); fd = -1; throw Failure.socket("path too long")
        }
        withUnsafeMutableBytes(of: &address.sun_path) { raw in
            raw.copyBytes(from: bytes)
        }
        let status = withUnsafePointer(to: &address) { ptr in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        guard status == 0 else {
            Darwin.close(fd); fd = -1; throw Failure.socket("connect failed")
        }
        #else
        throw Failure.unsupportedPlatform
        #endif
    }

    func request(_ request: BridgeRequest) throws -> BridgeEnvelope {
        #if os(macOS)
        guard fd >= 0 else { throw Failure.closed }
        let body = try JSONEncoder().encode(request)
        guard body.count <= 65_519 else { throw Failure.oversizedFrame }
        var length = UInt32(body.count).littleEndian
        try withUnsafeBytes(of: &length) { try writeAll($0) }
        try body.withUnsafeBytes { try writeAll($0) }
        var prefix = [UInt8](repeating: 0, count: 4)
        try prefix.withUnsafeMutableBytes { try readAll($0) }
        let count = prefix.withUnsafeBytes { $0.loadUnaligned(as: UInt32.self).littleEndian }
        guard count <= 65_519 else { throw Failure.oversizedFrame }
        var payload = [UInt8](repeating: 0, count: Int(count))
        try payload.withUnsafeMutableBytes { try readAll($0) }
        let response = try JSONDecoder().decode(BridgeEnvelope.self, from: Data(payload))
        guard response.re == request.id else { throw Failure.unexpectedResponse }
        return response
        #else
        throw Failure.unsupportedPlatform
        #endif
    }

    #if os(macOS)
    private func writeAll(_ buffer: UnsafeRawBufferPointer) throws {
        var offset = 0
        while offset < buffer.count {
            let n = Darwin.write(fd, buffer.baseAddress!.advanced(by: offset), buffer.count - offset)
            if n < 0 && errno == EINTR { continue }
            guard n > 0 else { throw Failure.closed }
            offset += n
        }
    }
    private func readAll(_ buffer: UnsafeMutableRawBufferPointer) throws {
        var offset = 0
        while offset < buffer.count {
            let n = Darwin.read(fd, buffer.baseAddress!.advanced(by: offset), buffer.count - offset)
            if n < 0 && errno == EINTR { continue }
            guard n > 0 else { throw Failure.closed }
            offset += n
        }
    }
    #endif
    func disconnect() {
        #if os(macOS)
        if fd >= 0 { Darwin.close(fd); fd = -1 }
        #endif
    }
}
