@testable import AgentBridgeAppleCore
import Security
import Testing

@Test func missingKeychainEntitlementIdentifiesTheAppBuildInsteadOfDeviceLock() throws {
    let message = try #require(BridgeKeychain.failure(errSecMissingEntitlement).errorDescription)
    #expect(message.contains("-34018"))
    #expect(message.contains("required Keychain entitlement"))
    #expect(message.contains("correctly signed build"))
    #expect(!message.lowercased().contains("unlock"))
}

@Test func unavailableKeychainInteractionSuggestsUnlockingAndReturningToTheApp() throws {
    let message = try #require(BridgeKeychain.failure(errSecInteractionNotAllowed).errorDescription)
    #expect(message.contains("-25308"))
    #expect(message.contains("Unlock this device"))
    #expect(message.contains("return to Agent Bridge"))
    #expect(!message.contains("signed build"))
}

@Test(arguments: [errSecNotAvailable, errSecAuthFailed, errSecUserCanceled, errSecParam, OSStatus(-12345)])
func otherKeychainFailuresDoNotClaimTheDeviceIsLocked(_ status: OSStatus) throws {
    let message = try #require(BridgeKeychain.failure(status).errorDescription)
    #expect(message.contains("\(status)"))
    #expect(!message.lowercased().contains("unlock"))
    #expect(!message.contains("signed build"))
}
