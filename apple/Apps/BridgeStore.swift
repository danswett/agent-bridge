import AgentBridgeAppleCore
import Foundation
import Observation

@MainActor @Observable
final class BridgeStore {
    private(set) var sessions: [BridgeSession] = []
    private(set) var connectionStatus = "Not connected"
    private(set) var machineName = "This Mac"
    private(set) var discoveryHold: String?
    private(set) var readOnlyReason: String?
    private(set) var isConnecting = false
    private(set) var isLive = false
    private(set) var isSample = false
    var profile = UserDefaults.standard.string(forKey: "bridgeProfile") ?? "default"

    @ObservationIgnored private var connectionTask: Task<Void, Never>?
    @ObservationIgnored private var snapshotTimer: Task<Void, Never>?
    @ObservationIgnored private var generation = UUID()

    func connect() {
        disconnect()
        if isSample {
            sessions = []
            machineName = "This Mac"
        }
        isSample = false
        #if os(macOS)
            let token = generation
            isConnecting = true
            connectionStatus = "Connecting…"
            connectionTask = Task { await runConnection(generation: token) }
        #else
            connectionStatus = "Remote connection unavailable"
        #endif
    }

    func disconnect() {
        generation = UUID()
        connectionTask?.cancel()
        connectionTask = nil
        snapshotTimer?.cancel()
        snapshotTimer = nil
        isConnecting = false
        isLive = false
        connectionStatus = isSample ? "Sample sessions · not connected" : sessions.isEmpty ? "Not connected" : "Disconnected · last received sessions"
    }

    #if os(macOS)
        private func runConnection(generation token: UUID) async {
            let transport = LocalBridge()
            defer { Task { await transport.disconnect() } }
            do {
                try Task.checkCancellation()
                guard token == generation else { return }
                let path = try LocalEndpoint.socketPath(profile: profile)
                UserDefaults.standard.set(profile, forKey: "bridgeProfile")
                let messages = try await transport.connect(path: path)
                let hello = try await transport.request(.init(id: 1, op: "hello"))
                try check(hello)
                guard hello.result?["protocol"] == .number(1) else { throw Failure.response("This host uses an unsupported protocol.") }
                guard hello.result?["capabilities"]?.array?.contains(.string("sessions.read")) == true else {
                    throw Failure.response("This host does not offer session access.")
                }
                guard token == generation else { return }
                let connectedMachine = hello.result?["machine"]?["name"]?.string ?? "This Mac"
                let connectedReadOnlyReason = hello.result?["machine"]?["readOnly"]?.string
                var state = SessionStore()
                var requestID = 2
                state.expectSubscription(requestID)
                let response = try await transport.request(.init(id: requestID, op: "sessions.subscribe"))
                try check(response)
                try Task.checkCancellation()
                guard token == generation else { return }
                connectionStatus = "Syncing sessions…"
                armSnapshotDeadline(transport, generation: token)
                for try await envelope in messages {
                    try Task.checkCancellation()
                    guard token == generation else { return }
                    if !state.receive(envelope) {
                        isLive = false
                        connectionStatus = "Resyncing sessions…"
                        requestID += 1
                        state.expectSubscription(requestID)
                        let response = try await transport.request(.init(id: requestID, op: "sessions.subscribe"))
                        try check(response)
                        try Task.checkCancellation()
                        guard token == generation else { return }
                        armSnapshotDeadline(transport, generation: token)
                    }
                    if state.isSynchronized {
                        snapshotTimer?.cancel()
                        snapshotTimer = nil
                        isConnecting = false
                        isLive = true
                        machineName = connectedMachine
                        readOnlyReason = connectedReadOnlyReason
                        sessions = state.orderedSessions
                        discoveryHold = state.discoveryHold
                        connectionStatus = "Live · \(sessions.count) \(sessions.count == 1 ? "session" : "sessions")"
                    }
                }
                throw Failure.response("The local host disconnected.")
            } catch {
                guard token == generation else { return }
                snapshotTimer?.cancel()
                snapshotTimer = nil
                isConnecting = false
                isLive = false
                connectionStatus = error.localizedDescription
            }
        }

        private func armSnapshotDeadline(_ transport: LocalBridge, generation token: UUID) {
            guard token == generation else { return }
            snapshotTimer?.cancel()
            snapshotTimer = Task {
                do { try await Task.sleep(for: .seconds(10)) } catch { return }
                guard token == generation else { return }
                // A host that answers subscribe but never completes its snapshot must
                // not leave the UI claiming a live, complete list.
                await transport.disconnect()
            }
        }

        private func check(_ envelope: BridgeEnvelope) throws {
            guard envelope.ok == true else { throw Failure.response(envelope.error ?? "The host refused the request.") }
        }
    #endif

    func showSamples() {
        disconnect()
        isSample = true
        machineName = "Sample workspace"
        readOnlyReason = nil
        discoveryHold = nil
        sessions = SampleSessions.sessions
        connectionStatus = "Sample sessions · not connected"
    }

    func closeSamples() {
        disconnect()
        isSample = false
        sessions = []
        machineName = "This Mac"
        connectionStatus = "Not connected"
    }

    enum Failure: LocalizedError {
        case response(String)
        var errorDescription: String? {
            if case let .response(message) = self {
                return message
            }
            return nil
        }
    }
}
