import Foundation
import Observation
import AgentBridgeAppleCore

@MainActor @Observable
final class BridgeStore {
    var sessions: [BridgeSession] = []
    var connectionStatus = "Not connected"
    var socketPath = NSString(string: "~/.agent-bridge/default/host.sock").expandingTildeInPath
    private let transport = LocalBridge()

    func refresh() async {
        #if os(macOS)
        do {
            try await transport.connect(path: socketPath)
            defer { Task { await transport.disconnect() } }
            let hello = try await transport.request(.init(id: 1, op: "hello"))
            guard hello.ok == true else { throw BridgeError.response(hello.error ?? "hello failed") }
            guard hello.result?["protocol"] == .number(1) else { throw BridgeError.response("Unsupported protocol") }
            var collected: [BridgeSession] = []
            var cursor: String?
            var nextID = 2
            repeat {
                let result = try await transport.request(.init(id: nextID, op: "sessions.list", data: cursor.map { ["cursor": $0] } ?? [:]))
                guard result.ok == true else { throw BridgeError.response(result.error ?? "sessions.list failed") }
                collected += (result.result?["sessions"]?.array ?? []).compactMap(BridgeSession.init(json:))
                cursor = result.result?["next"]?.string
                nextID += 1
            } while cursor != nil
            sessions = collected
            connectionStatus = "Connected locally · \(collected.count) sessions"
        } catch {
            connectionStatus = "Local host unavailable: \(error.localizedDescription)"
        }
        #else
        connectionStatus = "Remote pairing and Noise transport not yet implemented"
        #endif
    }

    enum BridgeError: Error { case response(String) }
}
