import AgentBridgeAppleCore
import Foundation
import Observation

struct HostTransport: Sendable {
    let messages: AsyncThrowingStream<BridgeEnvelope, any Error>
    let send: @Sendable (BridgeRequest, Duration) async throws -> BridgeEnvelope
    let close: @Sendable () async -> Void
    let peerKey: Data?
}

struct MutationAttempt {
    let operation: BridgeOperation
    let hostInstance: String
    let firstSentAt = ContinuousClock.now
    var isSending = true
    var result: JSONValue?
    var error: String?
    var canRetry: Bool {
        let limit: Duration = operation.op == "launch.trust.answer" ? .seconds(4 * 60) : operation.data["transfer"] == nil ? .seconds(9 * 60) : .seconds(20 * 60)
        return uncertain && result == nil && firstSentAt.duration(to: .now) < limit
    }

    var uncertain: Bool {
        guard !isSending else { return false }
        let known: Set<String> = switch operation.op {
        case "session.answer", "session.reply": ["delivered", "stale", "busy", "refused"]
        case "session.stop": ["ended", "confirm", "stale", "busy", "refused", "failed"]
        case "session.launch": ["started", "failed", "busy", "refused"]
        case "launch.trust.answer": ["started", "declined", "refused"]
        case "update.check", "update.install": ["accepted", "refused"]
        default: []
        }
        return !known.contains(result?["outcome"]?.string ?? "")
    }
}

@MainActor @Observable
final class HostStore: Identifiable {
    let id: String
    let isLocal: Bool
    var name: String
    private(set) var sessions: [BridgeSession] = []
    private(set) var endedSessions: [BridgeSession] = []
    private(set) var hello: JSONValue?
    private(set) var usage: JSONValue? {
        didSet {
            if oldValue != usage {
                onUsageChanged?(usage)
            }
        }
    }

    private(set) var update: JSONValue?
    private(set) var isLive = false
    private(set) var isConnecting = false
    private(set) var status = "Not connected"
    private(set) var discoveryHold: String?
    private(set) var connectionID = UUID()
    private(set) var attempts: [String: MutationAttempt] = [:]
    var replyDrafts: [String: String] = [:]
    var attachmentDrafts: [String: [(UUID, String)]] = [:]
    var onSessionsChanged: (@MainActor ([BridgeSession], [BridgeSession], Bool) -> Void)?
    var onUsageChanged: (@MainActor (JSONValue?) -> Void)?
    var onDisconnected: (@MainActor () -> Void)?
    var onMutationResult: (@MainActor (BridgeOperation, JSONValue) -> Void)?

    @ObservationIgnored private var provider: @Sendable () async throws -> HostTransport
    @ObservationIgnored private var task: Task<Void, Never>?
    @ObservationIgnored private var refreshTask: Task<Void, Never>?
    @ObservationIgnored private var snapshotTimer: Task<Void, Never>?
    @ObservationIgnored private var transport: HostTransport?
    @ObservationIgnored private var generation = UUID()
    @ObservationIgnored private var nextID = 0

    init(id: String, name: String, isLocal: Bool, provider: @escaping @Sendable () async throws -> HostTransport) {
        self.id = id
        self.name = name
        self.isLocal = isLocal
        self.provider = provider
    }

    var capabilities: Set<String> {
        Set(hello?["capabilities"]?.array?.compactMap(\.string) ?? [])
    }

    var readOnlyReason: String? {
        hello?["machine"]?["readOnly"]?.string
    }

    var instance: String {
        hello?["machine"]?["instance"]?.string ?? connectionID.uuidString
    }

    var peerKey: Data? {
        transport?.peerKey
    }

    func can(_ capability: String) -> Bool {
        isLive && capabilities.contains(capability)
    }

    var isRunning: Bool {
        task != nil
    }

    func reconnect(provider: @escaping @Sendable () async throws -> HostTransport) {
        self.provider = provider
        start()
    }

    func perform(_ operation: BridgeOperation, slot: String) async {
        guard isLive, attempts[slot]?.isSending != true, attempts[slot]?.uncertain != true else { return }
        attempts[slot] = MutationAttempt(operation: operation, hostInstance: instance)
        await deliver(slot)
    }

    func retry(_ slot: String) async {
        guard let attempt = attempts[slot], attempt.canRetry, !attempt.isSending,
              attempt.hostInstance == instance, isLive else { return }
        attempts[slot]?.isSending = true
        attempts[slot]?.error = nil
        await deliver(slot)
    }

    func clearAttempt(_ slot: String) {
        guard attempts[slot]?.isSending != true, attempts[slot]?.uncertain != true else { return }
        attempts.removeValue(forKey: slot)
    }

    func reconcileLaunchTrust(questionID: String, result: JSONValue) {
        guard attempts["launch"]?.result?["trust"]?["id"]?.string == questionID,
              let outcome = result["outcome"]?.string, ["started", "declined"].contains(outcome) else { return }
        var resolved: [String: JSONValue] = ["outcome": .string(outcome == "started" ? "started" : "failed")]
        resolved["session"] = result["session"]
        resolved["reason"] = result["reason"] ?? .string(outcome == "started" ? "Folder trusted and session started." : "Folder trust was declined.")
        attempts["launch"]?.result = .object(resolved)
        attempts["launch"]?.error = nil
    }

    private func deliver(_ slot: String) async {
        guard let attempt = attempts[slot] else { return }
        do {
            let timeout: Duration = attempt.operation.data["transfer"] != nil ? BridgeTransfer.timeout : .seconds(120)
            let result = try await call(attempt.operation.op, data: attempt.operation.data,
                                        timeout: timeout, onlyTo: attempt.hostInstance)
            attempts[slot]?.result = result
            onMutationResult?(attempt.operation, result)
        } catch { attempts[slot]?.error = "Delivery was not confirmed. Check the session before trying again. \(error.localizedDescription)" }
        attempts[slot]?.isSending = false
    }

    func start() {
        stop()
        let token = generation
        isConnecting = true
        task = Task { await run(generation: token) }
    }

    func stop() {
        attachmentDrafts = [:]
        generation = UUID()
        connectionID = UUID()
        task?.cancel()
        task = nil
        refreshTask?.cancel()
        snapshotTimer?.cancel()
        let old = transport
        transport = nil
        if let old {
            Task { await old.close() }
        }
        isLive = false
        isConnecting = false
        onDisconnected?()
        status = sessions.isEmpty ? "Not connected" : "Disconnected · last received sessions"
    }

    func call(_ op: String, data: [String: JSONValue] = [:], timeout: Duration = .seconds(15),
              onlyOn connection: UUID? = nil, onlyTo hostInstance: String? = nil) async throws -> JSONValue
    {
        guard isLive, let transport, connection == nil || connection == connectionID,
              hostInstance == nil || hostInstance == instance else { throw RelayError.disconnected }
        let token = connectionID
        let result = try await send(op, data: data, transport: transport, timeout: timeout)
        guard token == connectionID, isLive else {
            throw RelayError.refused("The connection changed before the result was confirmed. Check the session before trying again.")
        }
        return result
    }

    func refreshUsage() async {
        guard can("usage") else { return }
        let token = connectionID
        do { usage = try await call("usage.get", data: ["shared": .bool(capabilities.contains("usage.shared"))]) }
        catch {
            if token == connectionID {
                status = error.localizedDescription
            }
        }
    }

    private func allocate() -> Int {
        nextID += 1; return nextID
    }

    private func send(_ op: String, data: [String: JSONValue] = [:], transport: HostTransport,
                      timeout: Duration = .seconds(15)) async throws -> JSONValue
    {
        let response = try await transport.send(.init(id: allocate(), op: op, data: data), timeout)
        guard response.ok == true else { throw RelayError.refused(response.error ?? "The host refused the request.") }
        return response.result ?? .null
    }

    private func run(generation token: UUID) async {
        var delay = 1
        while !Task.isCancelled, token == generation {
            var opened: HostTransport?
            do {
                isConnecting = true
                status = "Connecting…"
                let current = try await provider()
                opened = current
                try Task.checkCancellation()
                guard token == generation else { await current.close(); return }
                transport = current
                connectionID = UUID()
                let attempt = connectionID
                nextID = 0
                let greeting = try await send("hello", transport: current)
                guard greeting["protocol"] == .number(1),
                      greeting["capabilities"]?.array?.contains(.string("sessions.read")) == true
                else {
                    throw RelayError.refused("This host does not offer compatible session access.")
                }
                try Task.checkCancellation()
                guard token == generation else { await current.close(); return }
                hello = greeting
                name = greeting["machine"]?["name"]?.string ?? name
                var state = SessionStore()
                var initialSnapshot = true
                try await subscribe(&state, transport: current, generation: token)
                if capabilities.contains("usage") {
                    let report = try await send("usage.subscribe", data: ["shared": .bool(capabilities.contains("usage.shared"))], transport: current)
                    guard token == generation else { await current.close(); return }
                    usage = report
                }
                if capabilities.contains("update") {
                    let state = try await send("update.subscribe", transport: current)
                    guard token == generation else { await current.close(); return }
                    update = state
                }
                refreshTask = Task {
                    do {
                        while !Task.isCancelled {
                            try await Task.sleep(for: .seconds(10))
                            let refreshed = try await send("hello", transport: current)
                            guard token == generation, attempt == connectionID else { return }
                            hello = refreshed
                            if !capabilities.contains("usage") {
                                usage = nil
                            }
                            if !capabilities.contains("update") {
                                update = nil
                            }
                        }
                    } catch { await current.close() }
                }
                for try await envelope in current.messages {
                    try Task.checkCancellation()
                    guard token == generation, attempt == connectionID else { break }
                    if envelope.ev == "usage.update" {
                        usage = envelope.data
                    }
                    if envelope.ev == "update.state" {
                        update = envelope.data
                    }
                    if !state.receive(envelope) {
                        isLive = false
                        status = "Resynchronizing…"
                        try await subscribe(&state, transport: current, generation: token)
                        if capabilities.contains("usage") {
                            let report = try await send("usage.subscribe", data: ["shared": .bool(capabilities.contains("usage.shared"))], transport: current)
                            guard token == generation else { await current.close(); return }
                            usage = report
                        }
                        if capabilities.contains("update") {
                            let report = try await send("update.subscribe", transport: current)
                            guard token == generation else { await current.close(); return }
                            update = report
                        }
                    }
                    if state.isSynchronized {
                        snapshotTimer?.cancel()
                        isConnecting = false
                        isLive = true
                        delay = 1
                        let previous = sessions
                        let latest = state.orderedSessions
                        if previous != latest {
                            // Preserve a small, clearly ended group, never actionable.
                            let currentIDs = Set(latest.map(\.id))
                            let removed = previous.filter { !currentIDs.contains($0.id) }
                            endedSessions = Array((removed + endedSessions).filter { !currentIDs.contains($0.id) }.prefix(20))
                            sessions = latest
                        }
                        if previous != latest || initialSnapshot {
                            onSessionsChanged?(previous, latest, initialSnapshot)
                        }
                        initialSnapshot = false
                        discoveryHold = state.discoveryHold
                        status = "Live · \(sessions.count) sessions"
                    }
                }
                throw RelayError.disconnected
            } catch {
                if let opened {
                    await opened.close()
                }
                guard token == generation else { return }
                transport = nil
                refreshTask?.cancel()
                snapshotTimer?.cancel()
                isLive = false
                isConnecting = false
                onDisconnected?()
                status = Task.isCancelled ? "Disconnected" : "\(error.localizedDescription) Reconnecting…"
                connectionID = UUID()
            }
            do { try await Task.sleep(for: .seconds(delay)) } catch { return }
            delay = min(delay * 2, 15)
        }
    }

    private func subscribe(_ state: inout SessionStore, transport: HostTransport, generation token: UUID) async throws {
        let id = allocate()
        state.expectSubscription(id)
        let response = try await transport.send(.init(id: id, op: "sessions.subscribe"), .seconds(15))
        guard response.ok == true else { throw RelayError.refused(response.error ?? "Subscription was refused.") }
        try Task.checkCancellation()
        guard token == generation else { throw CancellationError() }
        status = "Synchronizing…"
        snapshotTimer?.cancel()
        snapshotTimer = Task {
            do { try await Task.sleep(for: .seconds(15)) } catch { return }
            guard token == generation else { return }
            await transport.close()
        }
    }
}
