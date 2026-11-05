import AgentBridgeAppleCore
import Foundation

private actor ProbePresence {
    private var hosts = Set<String>()
    private var failed = false
    private var task: Task<Void, Never>?

    func start(_ events: AsyncThrowingStream<RelayEvent, any Error>) {
        task = Task {
            do {
                for try await event in events {
                    if case let .presence(machines) = event {
                        hosts = Set(machines.filter { $0.kind == "host" && $0.online }.map { $0.id.lowercased() })
                    }
                }
            } catch {}
            failed = true
        }
    }

    func wait(hostID: String) async throws {
        let deadline = ContinuousClock.now.advanced(by: .seconds(20))
        while ContinuousClock.now < deadline {
            if hosts.contains(hostID.lowercased()) {
                return
            }
            guard !failed else { throw ProbeFailure(check: "presence.closed") }
            try await Task.sleep(for: .milliseconds(20))
        }
        throw ProbeFailure(check: "presence.timeout")
    }

    func stop() {
        task?.cancel(); task = nil
    }
}

private actor ProbeFeed {
    private var store = SessionStore()
    private var failed = false
    private var task: Task<Void, Never>?

    func start(_ messages: AsyncThrowingStream<BridgeEnvelope, any Error>) {
        task = Task {
            do {
                for try await message in messages {
                    guard store.receive(message) else { throw ProbeFailure(check: "stream.sequence") }
                }
            } catch {}
            failed = true
        }
    }

    func expectSubscription(_ id: Int) {
        store.expectSubscription(id)
    }

    func wait(sessionID: String, marker: String) async throws -> BridgeSession {
        let deadline = ContinuousClock.now.advanced(by: .seconds(20))
        while ContinuousClock.now < deadline {
            if store.isSynchronized, let session = store.sessions[sessionID], session.response?.contains(marker) == true {
                return session
            }
            guard !failed else { throw ProbeFailure(check: "stream.closed") }
            try await Task.sleep(for: .milliseconds(20))
        }
        throw ProbeFailure(check: "stream.marker-timeout")
    }

    func stop() {
        task?.cancel()
        task = nil
    }
}

private actor ProbeConnection {
    private let send: @Sendable (BridgeRequest) async throws -> BridgeEnvelope
    private let disconnect: @Sendable () async -> Void
    private let feed = ProbeFeed()
    private var nextID = 0
    private(set) var capabilities = Set<String>()

    init(messages: AsyncThrowingStream<BridgeEnvelope, any Error>,
         send: @escaping @Sendable (BridgeRequest) async throws -> BridgeEnvelope,
         disconnect: @escaping @Sendable () async -> Void) async
    {
        self.send = send
        self.disconnect = disconnect
        await feed.start(messages)
    }

    func request(_ op: String, data: [String: JSONValue] = [:]) async throws -> JSONValue {
        nextID += 1
        let id = nextID
        if op == "sessions.subscribe" {
            await feed.expectSubscription(id)
        }
        let response = try await send(.init(id: id, op: op, data: data))
        guard response.re == id, response.ok == true, let result = response.result else { throw ProbeFailure(check: "request.response") }
        return result
    }

    func subscribe(hostID: String) async throws {
        let hello = try await request("hello")
        guard hello["protocol"] == .number(1), hello["machine"]?["id"]?.string?.lowercased() == hostID.lowercased() else {
            throw ProbeFailure(check: "hello.identity")
        }
        capabilities = Set(hello["capabilities"]?.array?.compactMap(\.string) ?? [])
        let mutations: Set = ["sessions.answer", "sessions.reply", "sessions.approve", "sessions.stop", "sessions.launch",
                              "sessions.attach", "sessions.attach.direct", "sessions.attach.blob",
                              "sessions.transfer", "sessions.transfer.serve", "launch.trust", "update"]
        guard capabilities.contains("sessions.read"), capabilities.isDisjoint(with: mutations) else { throw ProbeFailure(check: "hello.read-only") }
        _ = try await request("sessions.subscribe")
    }

    func wait(_ manifest: ProbeManifest, marker: String) async throws -> BridgeSession {
        try await feed.wait(sessionID: manifest.sessionID, marker: marker)
    }

    func checkDetail(_ manifest: ProbeManifest, marker: String) async throws {
        let result = try await request("session.get", data: ["id": .string(manifest.sessionID)])
        guard let session = BridgeSession(json: result), session.id == manifest.sessionID, session.response?.contains(marker) == true else {
            throw ProbeFailure(check: "detail.marker")
        }
        if let delegateID = manifest.delegateID {
            let value = try await request("session.get", data: ["id": .string(manifest.sessionID), "agent": .string(delegateID)])
            _ = try BridgeDelegatePresentation.validateDetail(value, expectedID: delegateID)
            guard let delegate = BridgeDelegate(json: value), delegate.id == delegateID else { throw ProbeFailure(check: "delegate.identity") }
        }
    }

    func checkRefusal(_ manifest: ProbeManifest) async throws {
        let result = try await request("session.reply", data: ["id": .string(manifest.sessionID),
                                                               "request": .string(UUID().uuidString.lowercased()),
                                                               "text": .string("Disposable interoperability refusal check")])
        guard result["outcome"]?.string == "refused" else { throw ProbeFailure(check: "mutation.not-refused") }
    }

    func close() async {
        await disconnect()
        await feed.stop()
    }
}

@MainActor
final class ProbeRunner {
    private let manifest: ProbeManifest
    private(set) var check = "manifest"
    private var connections: [ProbeConnection] = []
    private var relay: RelayBridge?
    private let presence = ProbePresence()

    init(manifest: ProbeManifest) {
        self.manifest = manifest
    }

    func run() async throws {
        do {
            #if os(macOS)
                check = "local.connect"
                let local = try await openLocal()
                try await inspect(local, name: "local", marker: manifest.initialMarker)

                check = "relay.sign-in"
                let flow = try RelaySignIn(relay: manifest.relayUrl)
                let code = try await flow.start()
                let account = try await flow.wait(for: code)
                guard account.accountId == manifest.accountID else { throw ProbeFailure(check: "sign-in.account") }
                evidence("relay.sign-in")

                check = "relay.proof-presence"
                let identity = NoiseKeyPair()
                let relay = try RelayBridge(relay: manifest.relayUrl, account: account, identity: identity, name: "Disposable Swift interop", kind: "app")
                self.relay = relay
                let events = try await relay.connect()
                await presence.start(events)
                try await presence.wait(hostID: manifest.hostDeviceID)
                evidence("relay.proof-presence")

                check = "remote.noise"
                let remote = try await openRemote(relay)
                try await inspect(remote, name: "remote", marker: manifest.initialMarker)
                check = "fixture.advance"
                try manifest.triggerUpdate()
                for (name, connection) in [("local", local), ("remote", remote)] {
                    check = "\(name).live-update"
                    _ = try await connection.wait(manifest, marker: manifest.updatedMarker)
                    try await connection.checkDetail(manifest, marker: manifest.updatedMarker)
                    evidence(check)
                }
                await local.close()
                await remote.close()
                check = "local.reconnect"
                let reconnectedLocal = try await openLocal()
                try await inspect(reconnectedLocal, name: "local.reconnect", marker: manifest.updatedMarker)
                check = "remote.reconnect"
                let reconnectedRemote = try await openRemote(relay)
                try await inspect(reconnectedRemote, name: "remote.reconnect", marker: manifest.updatedMarker)
                evidence("blob.integrity", status: "skipped", reason: "read-only host does not advertise attachments")
                await close()
                evidence("interop.complete")
            #else
                throw ProbeFailure(check: "platform.macos-required")
            #endif
        } catch {
            await close()
            throw error
        }
    }

    #if os(macOS)
        private func openLocal() async throws -> ProbeConnection {
            let bridge = LocalBridge()
            let messages = try await bridge.connect(path: manifest.localSocketPath)
            let connection = await ProbeConnection(messages: messages, send: { try await bridge.request($0) }, disconnect: { await bridge.disconnect() })
            connections.append(connection)
            return connection
        }
    #endif

    private func openRemote(_ relay: RelayBridge) async throws -> ProbeConnection {
        let channel = try await relay.open(peerID: manifest.hostDeviceID)
        guard try RelayWire.deviceID(publicKey: channel.peerKey).lowercased() == manifest.hostDeviceID.lowercased() else {
            throw ProbeFailure(check: "noise.peer-identity")
        }
        let connection = await ProbeConnection(messages: channel.messages, send: { try await channel.request($0) }, disconnect: { await channel.disconnect() })
        connections.append(connection)
        return connection
    }

    private func inspect(_ connection: ProbeConnection, name: String, marker: String) async throws {
        check = "\(name).snapshot-detail"
        try await connection.subscribe(hostID: manifest.hostDeviceID)
        _ = try await connection.wait(manifest, marker: marker)
        try await connection.checkDetail(manifest, marker: marker)
        evidence(check)
        check = "\(name).read-only-refusal"
        try await connection.checkRefusal(manifest)
        evidence(check)
    }

    private func close() async {
        for connection in connections {
            await connection.close()
        }
        connections.removeAll()
        await relay?.disconnect()
        relay = nil
        await presence.stop()
    }
}

func evidence(_ check: String, status: String = "pass", reason: String? = nil) {
    var record = ["check": check, "status": status]
    record["reason"] = reason
    guard let encoded = try? JSONEncoder().encode(record) else { return }
    try? FileHandle.standardOutput.write(contentsOf: encoded + Data([10]))
}
