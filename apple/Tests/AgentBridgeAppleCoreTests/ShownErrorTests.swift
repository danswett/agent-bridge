@testable import AgentBridgeAppleCore
import Foundation
import Testing

private struct RawError: LocalizedError {
    var errorDescription: String? { "token=ghp_FIXTURESECRET0123456789 /Users/fixture/.ssh" }
}

@Test func shownErrorsNeverCarryRawText() {
    let secret = "fixture-secret-AKIAFIXTURE"
    let errors: [any Error] = [
        RawError(), URLError(.cannotConnectToHost), RelayError.refused(secret), DecodingError.dataCorrupted(.init(codingPath: [], debugDescription: secret)),
    ]
    for error in errors {
        for shown in [BridgeShownError.sentence(for: error), BridgeShownError.signInSentence(for: error)] {
            #expect(!shown.contains("fixture"))
            #expect(!shown.contains("ghp_"))
            #expect(!shown.contains("/Users"))
        }
    }
    #expect(BridgeShownError.sentence(for: RelayError.refused(secret)) == BridgeShownError.refused)
    #expect(BridgeShownError.sentence(for: RawError()) == BridgeShownError.unknown)
    #expect(BridgeShownError.signInSentence(for: RelayError.refused(secret)) == BridgeShownError.signInRefused)
    #expect(BridgeShownError.signInSentence(for: RawError()) == BridgeShownError.signInFailed)
}

@Test func shownErrorsKeepFixedLocalSentences() {
    #expect(BridgeShownError.sentence(for: RelayError.disconnected) == "The relay connection ended.")
    #expect(BridgeShownError.sentence(for: BridgeOperationError("The session is missing.")) == "The session is missing.")
}
