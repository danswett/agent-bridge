#if os(macOS)
    @testable import AgentBridgeAppleCore
    import Darwin
    import Foundation
    import Testing

    /// Isolated socket fixture; never opens an installed host or a real profile.
    private final class SocketFixture: Sendable {
        let path: String
        let listener: Int32
        let serving: Task<Void, any Error>

        init(serve: @escaping @Sendable (Int32) throws -> Void) throws {
            path = "/tmp/ab-\(UUID().uuidString).sock"
            listener = Darwin.socket(AF_UNIX, SOCK_STREAM, 0)
            guard listener >= 0 else { throw Failure.io }
            var address = sockaddr_un()
            address.sun_family = sa_family_t(AF_UNIX)
            let bytes = Array(path.utf8) + [0]
            withUnsafeMutableBytes(of: &address.sun_path) { $0.copyBytes(from: bytes) }
            let fd = listener
            let bound = withUnsafePointer(to: &address) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    Darwin.bind(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
                }
            }
            guard bound == 0, Darwin.listen(fd, 1) == 0 else {
                Darwin.close(fd)
                throw Failure.io
            }
            serving = Task.detached {
                let peer = Darwin.accept(fd, nil, nil)
                guard peer >= 0 else { throw Failure.io }
                defer { Darwin.close(peer) }
                var flag: Int32 = 1
                _ = withUnsafePointer(to: &flag) { setsockopt(peer, SOL_SOCKET, SO_NOSIGPIPE, $0, socklen_t(MemoryLayout<Int32>.size)) }
                try serve(peer)
            }
        }

        deinit {
            Darwin.shutdown(listener, SHUT_RDWR)
            Darwin.close(listener)
            unlink(path)
        }

        static func request(_ fd: Int32) throws -> JSONValue {
            let prefix = try read(fd, count: 4)
            let count = prefix.enumerated().reduce(0) { $0 | (Int($1.element) << ($1.offset * 8)) }
            return try JSONDecoder().decode(JSONValue.self, from: read(fd, count: count))
        }

        static func read(_ fd: Int32, count: Int) throws -> Data {
            var bytes = [UInt8](repeating: 0, count: count)
            try bytes.withUnsafeMutableBytes { raw in
                var offset = 0
                while offset < count {
                    let n = Darwin.read(fd, raw.baseAddress!.advanced(by: offset), count - offset)
                    guard n > 0 else { throw Failure.io }
                    offset += n
                }
            }
            return Data(bytes)
        }

        static func write(_ fd: Int32, data: Data, fragment: Int = .max) throws {
            try data.withUnsafeBytes { raw in
                var offset = 0
                while offset < data.count {
                    let n = Darwin.write(fd, raw.baseAddress!.advanced(by: offset), min(fragment, data.count - offset))
                    guard n > 0 else { throw Failure.io }
                    offset += n
                }
            }
        }

        static func frame(_ json: String) throws -> Data {
            try LocalFraming.encode(Data(json.utf8))
        }

        enum Failure: Error { case io }
    }

    @Test(.timeLimit(.minutes(1))) func localReaderPreservesFragmentedEventAndResponseOrder() async throws {
        let fixture = try SocketFixture { fd in
            _ = try SocketFixture.request(fd)
            var bytes = try SocketFixture.frame(#"{"ev":"future.event","seq":1,"data":{}}"#)
            try bytes.append(SocketFixture.frame(#"{"re":1,"ok":true,"result":{"protocol":1}}"#))
            try bytes.append(SocketFixture.frame(#"{"ev":"sessions.synced","seq":2,"data":{}}"#))
            try SocketFixture.write(fd, data: bytes, fragment: 3)
        }
        let bridge = LocalBridge()
        let stream = try await bridge.connect(path: fixture.path)
        let response = try await bridge.request(.init(id: 1, op: "hello"))
        #expect(response.result?["protocol"] == .number(1))
        var received: [BridgeEnvelope] = []
        do { for try await message in stream {
            received.append(message)
        } } catch {}
        #expect(received.count == 3)
        #expect(received.first?.ev == "future.event")
        #expect(received.dropFirst().first?.re == 1)
        #expect(received.last?.ev == "sessions.synced")
        try await fixture.serving.value
    }

    @Test(.timeLimit(.minutes(1))) func concurrentRequestsAreMatchedByIDNotArrivalOrder() async throws {
        let fixture = try SocketFixture { fd in
            let first = try SocketFixture.request(fd)
            let second = try SocketFixture.request(fd)
            for request in [second, first] {
                let id = Int(request["id"]!.number!)
                try SocketFixture.write(fd, data: SocketFixture.frame(#"{"re":\#(id),"ok":true,"result":{}}"#))
            }
        }
        let bridge = LocalBridge()
        let stream = try await bridge.connect(path: fixture.path)
        async let first = bridge.request(BridgeRequest(id: 1, op: "hello"))
        async let second = bridge.request(BridgeRequest(id: 2, op: "sessions.list"))
        let results = try await (first, second)
        #expect(results.0.re == 1)
        #expect(results.1.re == 2)
        withExtendedLifetime(stream) {}
        try await fixture.serving.value
    }

    @Test(.timeLimit(.minutes(1))) func cancellingSilentRequestClosesItsChannel() async throws {
        let signal = AsyncStream<Void>.makeStream()
        let fixture = try SocketFixture { fd in
            _ = try SocketFixture.request(fd)
            signal.continuation.yield(())
            signal.continuation.finish()
            var byte: UInt8 = 0
            #expect(Darwin.read(fd, &byte, 1) == 0)
        }
        let bridge = LocalBridge()
        let stream = try await bridge.connect(path: fixture.path)
        let request = Task { try await bridge.request(.init(id: 1, op: "hello")) }
        for await _ in signal.stream {
            break
        }
        request.cancel()
        do {
            _ = try await request.value
            Issue.record("Cancelled request succeeded")
        } catch { #expect(error is CancellationError) }
        withExtendedLifetime(stream) {}
        try await fixture.serving.value
    }

    @Test(.timeLimit(.minutes(1))) func silentHostTimesOutWithoutBlocking() async throws {
        let fixture = try SocketFixture { fd in
            _ = try SocketFixture.request(fd)
            var byte: UInt8 = 0
            #expect(Darwin.read(fd, &byte, 1) == 0)
        }
        let bridge = LocalBridge()
        let stream = try await bridge.connect(path: fixture.path)
        do {
            _ = try await bridge.request(.init(id: 1, op: "hello"), timeout: .milliseconds(100))
            Issue.record("Silent host unexpectedly responded")
        } catch LocalBridge.Failure.timeout {}
        withExtendedLifetime(stream) {}
        try await fixture.serving.value
    }

    @Test(.timeLimit(.minutes(1)), arguments: [Data([0xFF, 0xFF, 0xFF, 0x7F]), Data([10, 0, 0, 0, 123])])
    func malformedOrTruncatedFrameFailsPendingRequest(bytes: Data) async throws {
        let fixture = try SocketFixture { fd in
            _ = try SocketFixture.request(fd)
            try SocketFixture.write(fd, data: bytes)
        }
        let bridge = LocalBridge()
        let stream = try await bridge.connect(path: fixture.path)
        await #expect(throws: (any Error).self) { try await bridge.request(.init(id: 1, op: "hello")) }
        withExtendedLifetime(stream) {}
        try await fixture.serving.value
    }

    @Test(.timeLimit(.minutes(1))) func reconnectUsesAFreshChannelAfterFailure() async throws {
        let bridge = LocalBridge()
        await #expect(throws: (any Error).self) { try await bridge.connect(path: "/tmp/ab-missing-\(UUID().uuidString).sock") }
        let fixture = try SocketFixture { fd in
            _ = try SocketFixture.request(fd)
            try SocketFixture.write(fd, data: SocketFixture.frame(#"{"re":1,"ok":true,"result":{}}"#))
        }
        let stream = try await bridge.connect(path: fixture.path)
        #expect(try await bridge.request(.init(id: 1, op: "hello")).ok == true)
        withExtendedLifetime(stream) {}
        try await fixture.serving.value
    }
#endif
