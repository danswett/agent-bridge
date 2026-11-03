@testable import AgentBridgeInterop
import Foundation
import Testing

private func fixture() throws -> URL {
    let root = URL(fileURLWithPath: "/tmp").appendingPathComponent("ab-i-\(UUID().uuidString)")
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
    return root
}

private func manifest(_ root: URL, relay: String = "https://localhost:8443", socket: String? = nil, trigger: String? = nil) -> ProbeManifest {
    ProbeManifest(relayUrl: relay, localSocketPath: socket ?? root.appendingPathComponent("host.sock").path,
                  accountID: "9001", hostDeviceID: String(repeating: "a", count: 32), fixtureRoot: root.path,
                  sessionID: "interop-session", initialMarker: "before", updatedMarker: "after",
                  updateTriggerPath: trigger ?? root.appendingPathComponent("advance").path, delegateID: "delegate-1")
}

@Test func probeRefusesPublicRelaysAndURLCredentialsBeforeMakingAnyConnection() throws {
    let root = try fixture()
    defer { try? FileManager.default.removeItem(at: root) }
    try manifest(root).validate()
    for url in ["https://relay.example", "http://localhost:8443", "https://user:secret@localhost:8443", "https://localhost:8443/path"] {
        #expect(throws: (any Error).self) { try manifest(root, relay: url).validate() }
    }
}

@Test func probePathsCannotReachAnInstalledProfileOrEscapeThroughASymlink() throws {
    let root = try fixture()
    let outside = try fixture()
    defer {
        try? FileManager.default.removeItem(at: root)
        try? FileManager.default.removeItem(at: outside)
    }
    #expect(throws: (any Error).self) { try manifest(root, socket: outside.appendingPathComponent("host.sock").path).validate() }
    #expect(throws: (any Error).self) { try manifest(root, trigger: root.appendingPathComponent("../outside").path).validate() }
    let link = root.appendingPathComponent("linked")
    try FileManager.default.createSymbolicLink(at: link, withDestinationURL: outside)
    #expect(throws: (any Error).self) { try manifest(root, trigger: link.appendingPathComponent("advance").path).validate() }
}

@Test func probeUpdateTriggerIsPrivateAndNeverOverwritesAnExistingFile() throws {
    let root = try fixture()
    defer { try? FileManager.default.removeItem(at: root) }
    let configuration = manifest(root)
    try configuration.triggerUpdate()
    let file = URL(fileURLWithPath: configuration.updateTriggerPath)
    #expect(try String(contentsOf: file, encoding: .utf8) == "advance\n")
    #expect(try (FileManager.default.attributesOfItem(atPath: file.path)[.posixPermissions] as? NSNumber)?.intValue == 0o600)
    #expect(throws: (any Error).self) { try configuration.triggerUpdate() }
    #expect(try String(contentsOf: file, encoding: .utf8) == "advance\n")
}

@Test func probeManifestMustBeABoundedPrivateRegularFile() throws {
    let root = try fixture()
    defer { try? FileManager.default.removeItem(at: root) }
    let file = root.appendingPathComponent("manifest.json")
    let fields = ["relayUrl": "https://localhost:8443", "localSocketPath": root.appendingPathComponent("host.sock").path,
                  "accountID": "9001", "hostDeviceID": String(repeating: "a", count: 32), "fixtureRoot": root.path,
                  "sessionID": "interop-session", "initialMarker": "before", "updatedMarker": "after",
                  "updateTriggerPath": root.appendingPathComponent("advance").path]
    try JSONEncoder().encode(fields).write(to: file)
    try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
    #expect(try ProbeManifest.load(file.path).accountID == "9001")
    try FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: file.path)
    #expect(throws: (any Error).self) { try ProbeManifest.load(file.path) }
    try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
    try Data(repeating: 32, count: 32769).write(to: file)
    #expect(throws: (any Error).self) { try ProbeManifest.load(file.path) }
    #expect(throws: (any Error).self) { try ProbeManifest.load(root.path) }
}
