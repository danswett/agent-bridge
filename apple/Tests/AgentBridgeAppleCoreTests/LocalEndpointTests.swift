#if os(macOS)
    @testable import AgentBridgeAppleCore
    import CryptoKit
    import Foundation
    import Testing

    @Test func profileMatchesHostValidation() throws {
        #expect(try LocalEndpoint.socketPath(profile: "work.mac", home: URL(fileURLWithPath: "/Users/test")) == "/Users/test/.agent-bridge/work.mac/host.sock")
        for profile in ["", ".", "..", "../other", "test/name", "é", String(repeating: "x", count: 65)] {
            #expect(throws: (any Error).self) { try LocalEndpoint.socketPath(profile: profile) }
        }
    }

    @Test func longProfileFolderUsesHostHashAndRuntimePrecedence() {
        let folder = "/Users/" + String(repeating: "long", count: 30)
        let hash = SHA256.hash(data: Data(folder.utf8)).prefix(8).map { String(format: "%02x", $0) }.joined()
        #expect(LocalEndpoint.socketPath(folder: folder, environment: ["XDG_RUNTIME_DIR": "/tmp", "TMPDIR": "/var/tmp"]) == "/tmp/agent-bridge-\(hash).sock")
        #expect(LocalEndpoint.socketPath(folder: folder, environment: [:]) == folder + "/host.sock")
    }
#endif
