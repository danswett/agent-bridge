@testable import AgentBridgeClientState
import Testing

@MainActor @Test func credentialReadFailureOffersConnectionRetryInsteadOfAnotherGrant() {
    let store = BridgeStore()
    store.recordSignInFailure(.credentialStorage, reconnect: true, message: "Could not read credentials")
    #expect(store.signInFailureStage == .credentialStorage)
    #expect(store.signInRecoveryReconnect)
    #expect(store.signInStatus == "Could not read credentials")
    store.recordSignInFailure(.browserApproval, reconnect: false, message: "Approval expired")
    #expect(store.signInFailureStage == .browserApproval)
    #expect(!store.signInRecoveryReconnect)
    #expect(store.signInStatus == "Approval expired")
}
