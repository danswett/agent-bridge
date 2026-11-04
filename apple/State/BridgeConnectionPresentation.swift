import Foundation

struct BridgeConnectionPresentation: Equatable {
    let summary: String
    let remote: String
    let local: String?
    let emptyTitle: String
    let emptyDetail: String

    init(connectedMachines: Int, sessions: Int, signedIn: Bool, signingIn: Bool,
         signInStatus: String?, relayStatus: String, relayConnected: Bool,
         receivedRoster: Bool, knownRemoteHosts: Int, onlineRemoteHosts: Int,
         localStatus: String?)
    {
        local = localStatus.map { "This Mac (optional): \($0)" }
        if signingIn {
            remote = signInStatus ?? "Complete GitHub sign-in to connect your remote machines."
        } else if relayConnected {
            if !receivedRoster {
                remote = "Connected to relay · waiting for the machine list"
            } else if knownRemoteHosts == 0 {
                remote = "Connected to relay · no remote hosts advertised for this account"
            } else {
                remote = "Connected to relay · \(onlineRemoteHosts) online of \(knownRemoteHosts) remote hosts"
            }
        } else {
            remote = signInStatus.flatMap { signedIn ? nil : $0 } ?? relayStatus
        }

        if connectedMachines > 0 {
            summary = "\(connectedMachines) \(connectedMachines == 1 ? "machine" : "machines") connected · \(sessions) sessions"
            emptyTitle = "No active sessions"
            emptyDetail = "Sessions appear when an agent starts on a connected machine."
        } else {
            summary = remote
            if signingIn {
                emptyTitle = "Finish GitHub sign-in"
                emptyDetail = remote
            } else if !signedIn {
                emptyTitle = "Connect to Agent Bridge"
                emptyDetail = remote
            } else if relayConnected, receivedRoster, knownRemoteHosts == 0 {
                emptyTitle = "No remote hosts found"
                emptyDetail = "The relay returned no Agent Bridge hosts for this account. Check the remote host's account, relay and connection status. A local host is not required."
            } else if relayConnected, receivedRoster, onlineRemoteHosts == 0 {
                emptyTitle = "Remote machines are offline"
                emptyDetail = "The relay knows your remote hosts, but none is online. A local host is not required."
            } else if relayConnected, receivedRoster {
                emptyTitle = "Connecting to remote machines"
                emptyDetail = "The relay lists \(onlineRemoteHosts) online remote hosts. Open Machines to see each host's connection status."
            } else {
                emptyTitle = "Remote connection"
                emptyDetail = remote
            }
        }
    }
}
