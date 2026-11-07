@testable import AgentBridgeAppleCore
import Foundation
import Testing

private struct RawError: LocalizedError {
    var errorDescription: String? { "token=ghp_FIXTURESECRET0123456789 /Users/fixture/.ssh" }
}

@Test func shownErrorsNeverCarryRawText() {
    let secret = "fixture-secret-AKIAFIXTURE"
    let errors: [any Error] = [
        RawError(), URLError(.cannotConnectToHost), RelayError.hostRefused, DecodingError.dataCorrupted(.init(codingPath: [], debugDescription: secret)),
        NSError(domain: secret, code: 1, userInfo: [NSLocalizedDescriptionKey: secret]),
    ]
    for error in errors {
        for shown in [BridgeShownError.sentence(for: error), BridgeShownError.signInSentence(for: error)] {
            #expect(!shown.contains("fixture"))
            #expect(!shown.contains("ghp_"))
            #expect(!shown.contains("/Users"))
        }
    }
    #expect(BridgeShownError.sentence(for: RelayError.hostRefused) == BridgeShownError.refused)
    #expect(BridgeShownError.sentence(for: RawError()) == BridgeShownError.unknown)
    #expect(BridgeShownError.signInSentence(for: RelayError.hostRefused) == BridgeShownError.signInRefused)
    #expect(BridgeShownError.signInSentence(for: RawError()) == BridgeShownError.signInFailed)
}

@Test func shownErrorsKeepAccurateLocalSentencesInsteadOfCallingThemHostRefusals() {
    #expect(BridgeShownError.sentence(for: RelayError.refused("The relay refused this request.")) == "The relay refused this request.")
    #expect(BridgeShownError.sentence(for: RelayError.refused("Keychain access failed. Unlock this device and try again.")) != BridgeShownError.refused)
    #expect(BridgeShownError.signInSentence(for: RelayError.refused("The code expired. Start sign-in again.")) == "The code expired. Start sign-in again.")
}

@Test func hostBusyTextNeverReachesTheShownLaunchError() throws {
    let secret = "busy token=ghp_FIXTURESECRET0123456789 /Users/fixture/.ssh"
    let options = BridgeLaunchOptions(json: .object(["busy": .string(secret)]))
    #expect(throws: BridgeOperationError.self) {
        try BridgeLaunchDraft().operation(options: options, capabilities: ["sessions.launch"])
    }
    do {
        _ = try BridgeLaunchDraft().operation(options: options, capabilities: ["sessions.launch"])
    } catch {
        let shown = BridgeShownError.sentence(for: error)
        #expect(!shown.contains("ghp_"))
        #expect(!shown.contains("/Users"))
        #expect(shown == "This machine is busy. Try again in a moment.")
    }
}

/// Raw error text can carry a secret or a file path, so no source that draws or reports a status
/// may read it. Errors reach the screen only through `BridgeShownError` (#826).
@Test func noSourceRendersRawErrorText() throws {
    let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
    let forbidden = ["localizedDescription", "String(describing: error)", "String(describing: failure)", "\\(error)", "\\(failure)", "\\(error.", "\\(failure."]
    // Empty on purpose: add a file here, with the reason, only for a non-UI use such as logging.
    let allowlist: Set<String> = []
    var scanned = 0
    for folder in ["Apps", "State", "Sources"] {
        let enumerator = try #require(FileManager.default.enumerator(at: root.appendingPathComponent(folder), includingPropertiesForKeys: nil))
        for case let file as URL in enumerator where file.pathExtension == "swift" && file.lastPathComponent != "BridgeShownError.swift" {
            guard !allowlist.contains(file.lastPathComponent) else { continue }
            let text = try String(contentsOf: file, encoding: .utf8)
            scanned += 1
            for pattern in forbidden {
                #expect(!text.contains(pattern), "\(file.lastPathComponent) renders raw error text via \(pattern)")
            }
        }
    }
    #expect(scanned > 20)
}

@Test func shownErrorsKeepFixedLocalSentences() {
    #expect(BridgeShownError.sentence(for: RelayError.disconnected) == "The relay connection ended.")
    #expect(BridgeShownError.sentence(for: BridgeOperationError("The session is missing.")) == "The session is missing.")
}
