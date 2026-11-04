@testable import AgentBridgeClientState
import Testing

private func presentation(connected: Int = 0, signedIn: Bool = true, signingIn: Bool = false,
                          signInStatus: String? = nil, relayStatus: String = "Signed in",
                          relayConnected: Bool = true, receivedRoster: Bool = true,
                          known: Int = 0, online: Int = 0, localStatus: String? = "Local host timed out") -> BridgeConnectionPresentation
{
    BridgeConnectionPresentation(connectedMachines: connected, sessions: 3, signedIn: signedIn,
                                 signingIn: signingIn, signInStatus: signInStatus, relayStatus: relayStatus,
                                 relayConnected: relayConnected, receivedRoster: receivedRoster,
                                 knownRemoteHosts: known, onlineRemoteHosts: online, localStatus: localStatus)
}

@Test func localFailuresCannotHideAnEmptyRemoteRosterOrAskASignedInUserToSignIn() {
    let result = presentation()
    #expect(result.summary == "Connected to relay · no remote hosts advertised for this account")
    #expect(result.emptyTitle == "No remote hosts found")
    #expect(result.emptyDetail.contains("A local host is not required."))
    #expect(result.local == "This Mac (optional): Local host timed out")
    #expect(presentation(localStatus: "Connecting…").summary == result.summary)
    #expect(presentation(localStatus: nil).summary == result.summary)
}

@Test func rosterNotReceivedIsDifferentFromAnEmptyRoster() {
    let result = presentation(receivedRoster: false)
    #expect(result.remote == "Connected to relay · waiting for the machine list")
    #expect(result.emptyTitle != "No remote hosts found")
}

@Test func relayErrorsRemainVisibleWhenALocalHostIsMissing() {
    let result = presentation(relayStatus: "The relay connection ended. Reconnecting…", relayConnected: false)
    #expect(result.summary == "The relay connection ended. Reconnecting…")
    #expect(result.emptyDetail == result.summary)
    #expect(!result.summary.contains("Local host"))
}

@Test func knownOfflineHostsAreNotReportedAsAnEmptyRoster() {
    let result = presentation(known: 2)
    #expect(result.emptyTitle == "Remote machines are offline")
    #expect(result.remote == "Connected to relay · 0 online of 2 remote hosts")
}

@Test func advertisedHostsAwaitingChannelsAreReportedBeforeAnySessionsArrive() {
    let result = presentation(known: 3, online: 2)
    #expect(result.emptyTitle == "Connecting to remote machines")
    #expect(result.emptyDetail.contains("2 online remote hosts"))
    #expect(result.remote == "Connected to relay · 2 online of 3 remote hosts")
}

@Test func connectedHostSummaryAndRemoteFailureCanBeShownTogether() {
    let result = presentation(connected: 1, relayStatus: "Relay unavailable", relayConnected: false)
    #expect(result.summary == "1 machine connected · 3 sessions")
    #expect(result.remote == "Relay unavailable")
    #expect(result.emptyTitle == "No active sessions")
    #expect(presentation(connected: 2).summary == "2 machines connected · 3 sessions")
}

@Test func activeOrFailedSignInIsNotHiddenByALocalRetry() {
    let pending = presentation(signedIn: false, signingIn: true, signInStatus: "Complete sign-in.",
                               relayStatus: "Not signed in", relayConnected: false, receivedRoster: false)
    #expect(pending.emptyTitle == "Finish GitHub sign-in")
    #expect(pending.summary == "Complete sign-in.")
    let failed = presentation(signedIn: false, signInStatus: "The code expired.", relayConnected: false)
    #expect(failed.emptyTitle == "Connect to Agent Bridge")
    #expect(failed.summary == "The code expired.")
}

@Test func oldSuccessfulSignInTextCannotHideANewRelayFailure() {
    let result = presentation(signInStatus: "Signed in as fixture", relayStatus: "Relay unavailable", relayConnected: false)
    #expect(result.summary == "Relay unavailable")
}
