import AgentBridgeAppleCore
import Foundation
#if os(macOS)
    import Darwin
#endif

struct ProbeFailure: Error {
    let check: String
}

/// This executable is deliberately limited to an explicitly created local stack.
/// It cannot be pointed at the user's normal relay or installed host profile.
struct ProbeManifest: Decodable, Sendable {
    let relayUrl: String
    let localSocketPath: String
    let accountID: String
    let hostDeviceID: String
    let fixtureRoot: String
    let sessionID: String
    let initialMarker: String
    let updatedMarker: String
    let updateTriggerPath: String
    let delegateID: String?

    static func load(_ path: String) throws -> Self {
        guard path.hasPrefix("/") else { throw ProbeFailure(check: "manifest.absolute-path") }
        let url = URL(fileURLWithPath: path)
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        guard attributes[.type] as? FileAttributeType == .typeRegular,
              let mode = attributes[.posixPermissions] as? NSNumber, mode.intValue & 0o077 == 0,
              let size = attributes[.size] as? NSNumber, (1 ... 32768).contains(size.intValue)
        else { throw ProbeFailure(check: "manifest.private-file") }
        let manifest = try JSONDecoder().decode(Self.self, from: Data(contentsOf: url))
        try manifest.validate()
        return manifest
    }

    func validate() throws {
        let relay = try RelayAddress.validated(relayUrl)
        guard ["localhost", "127.0.0.1", "[::1]", "::1"].contains(relay.host?.lowercased() ?? ""),
              RelayWire.isDeviceID(hostDeviceID),
              !accountID.isEmpty, accountID.utf8.count <= 256,
              !sessionID.isEmpty, sessionID.utf8.count <= 256,
              !initialMarker.isEmpty, !updatedMarker.isEmpty, initialMarker != updatedMarker,
              initialMarker.utf8.count <= 1024, updatedMarker.utf8.count <= 1024,
              delegateID.map({ !$0.isEmpty && $0.utf8.count <= 256 }) ?? true
        else { throw ProbeFailure(check: "manifest.scope") }
        let root = URL(fileURLWithPath: fixtureRoot).standardizedFileURL.resolvingSymlinksInPath()
        let temporaryRoots = [URL(fileURLWithPath: "/tmp"), FileManager.default.temporaryDirectory]
            .map { $0.standardizedFileURL.resolvingSymlinksInPath().path }
        guard fixtureRoot.hasPrefix("/"), root.path != "/",
              temporaryRoots.contains(where: { root.path.hasPrefix($0 + "/") }),
              localSocketPath.hasPrefix("/"), localSocketPath.utf8.count <= 103,
              updateTriggerPath.hasPrefix("/"),
              !localSocketPath.utf8.contains(0), !updateTriggerPath.utf8.contains(0),
              isChild(localSocketPath, of: root), isChild(updateTriggerPath, of: root)
        else { throw ProbeFailure(check: "manifest.paths") }
        let attributes = try FileManager.default.attributesOfItem(atPath: root.path)
        guard attributes[.type] as? FileAttributeType == .typeDirectory,
              let mode = attributes[.posixPermissions] as? NSNumber, mode.intValue & 0o077 == 0
        else { throw ProbeFailure(check: "manifest.private-root") }
    }

    private func isChild(_ path: String, of root: URL) -> Bool {
        let url = URL(fileURLWithPath: path).standardizedFileURL
        // Foundation may leave a nonexistent final component unresolved. Resolve
        // its existing parent separately so a linked directory cannot escape.
        let child = url.deletingLastPathComponent().resolvingSymlinksInPath().appendingPathComponent(url.lastPathComponent)
        if let attributes = try? FileManager.default.attributesOfItem(atPath: child.path),
           attributes[.type] as? FileAttributeType == .typeSymbolicLink
        {
            return false
        }
        return child.path.hasPrefix(root.path + "/")
    }

    func triggerUpdate() throws {
        try validate()
        #if os(macOS)
            // Never overwrite an existing file or follow a final symlink. A stack
            // has one update trigger; a repeated probe needs a fresh fixture run.
            let temporary = updateTriggerPath + ".\(UUID().uuidString).tmp"
            let descriptor = open(temporary, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, 0o600)
            guard descriptor >= 0 else { throw ProbeFailure(check: "fixture.create-trigger") }
            let file = FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
            defer { unlink(temporary) }
            try file.write(contentsOf: Data("advance\n".utf8))
            try file.close()
            // link publishes complete contents atomically and refuses an existing
            // destination, unlike rename or Data.write(.atomic), which replace it.
            guard link(temporary, updateTriggerPath) == 0 else { throw ProbeFailure(check: "fixture.create-trigger") }
        #else
            throw ProbeFailure(check: "platform.macos-required")
        #endif
    }
}
