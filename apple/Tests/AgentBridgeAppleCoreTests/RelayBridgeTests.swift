@testable import AgentBridgeAppleCore
import Foundation
import Testing

/// A disposable in-memory relay and host. It independently reads the actual
/// client's handshake/proof and decrypts every request in arrival order.
private actor MockRelaySocket: RelaySocket {
    enum Mode: Sendable { case echo, interleaved, reverseTwo, silent, tamper }

    private final class HostChannel {
        var noise: NoiseXXHandshake
        let id: Data
        var requests: [Int] = []
        init(noise: NoiseXXHandshake, id: Data) {
            self.noise = noise; self.id = id
        }
    }

    let hostKey = NoiseKeyPair()
    let challengeKey = NoiseKeyPair()
    let account = RelayAccount(token: "fixture-account-token", accountId: "github:fixture", login: "fixture")
    let mode: Mode
    let wrongAccount: Bool
    let wrongReadyDevice: Bool
    let ready: Bool
    let presenceBeforeReady: Bool
    let replyToHandshake: Bool
    let wrongHostKey: Bool
    private(set) var isClosed = false
    private(set) var proofAccepted = false
    private(set) var requestIDs: [Int] = []
    private(set) var closeFrames = 0
    private var clientID: String?
    private var clientPublicKey: Data?
    private var inbound: [RelaySocketMessage] = []
    private var receivers: [CheckedContinuation<RelaySocketMessage, any Error>] = []
    private var requestWaiters: [(Int, CheckedContinuation<Void, any Error>)] = []
    private var channels: [String: HostChannel] = [:]
    private var handshakes = 0
    private var handshakeWaiters: [CheckedContinuation<Void, any Error>] = []

    init(mode: Mode = .echo, wrongAccount: Bool = false, wrongReadyDevice: Bool = false,
         ready: Bool = true, presenceBeforeReady: Bool = false,
         replyToHandshake: Bool = true, wrongHostKey: Bool = false)
    {
        self.mode = mode
        self.wrongAccount = wrongAccount
        self.wrongReadyDevice = wrongReadyDevice
        self.ready = ready
        self.presenceBeforeReady = presenceBeforeReady
        self.replyToHandshake = replyToHandshake
        self.wrongHostKey = wrongHostKey
    }

    func makeBridge(identity: NoiseKeyPair = NoiseKeyPair()) -> RelayBridge {
        RelayBridge(socket: self, account: account, identity: identity)
    }

    func hostID() -> String {
        hostKey.deviceID
    }

    func send(_ message: RelaySocketMessage) throws {
        guard !isClosed else { throw RelayError.disconnected }
        switch message {
        case let .text(text):
            let json = try JSONDecoder().decode(JSONValue.self, from: Data(text.utf8))
            switch json["t"]?.string {
            case "hello":
                guard json["token"]?.string == account.token, let pub = json["pub"]?.string else { throw RelayError.invalidMessage }
                let publicKey = try RelayWire.unhex(pub)
                clientPublicKey = publicKey
                clientID = try RelayWire.deviceID(publicKey: publicKey)
                try control(["t": .string("challenge"), "eph": .string(RelayWire.hex(challengeKey.publicKey)),
                             "nonce": .string(RelayWire.hex(Data(repeating: 17, count: 32))),
                             "account": .string(wrongAccount ? "github:somebody-else" : account.accountId)])
            case "proof":
                guard let clientPublicKey, let clientID else { throw RelayError.invalidMessage }
                let expected = try RelayWire.proofMAC(sharedSecret: challengeKey.sharedSecret(with: clientPublicKey),
                                                      nonce: Data(repeating: 17, count: 32), accountID: account.accountId)
                guard json["mac"]?.string == RelayWire.hex(expected) else { throw RelayError.invalidMessage }
                proofAccepted = true
                if presenceBeforeReady {
                    try presence()
                }
                if ready {
                    try control(["t": .string("ready"), "device": .string(wrongReadyDevice ? hostKey.deviceID : clientID),
                                 "login": .string(account.login)])
                }
                if !presenceBeforeReady, ready {
                    try presence()
                }
            default: throw RelayError.invalidMessage
            }
        case let .data(data):
            guard let clientID else { throw RelayError.invalidMessage }
            let frame = try RelayWire.parseFrame(data)
            guard frame.peerID == hostKey.deviceID else { throw RelayError.invalidMessage }
            let key = RelayWire.hex(frame.channelID)
            switch frame.kind {
            case .msg1:
                var noise = try NoiseXXHandshake(role: .responder, staticKey: wrongHostKey ? NoiseKeyPair() : hostKey,
                                                 prologue: RelayWire.prologue(initiator: clientID, responder: hostKey.deviceID, channelID: frame.channelID),
                                                 expectedPeerID: clientID)
                _ = try noise.readMessage(frame.body)
                let intro = try JSONEncoder().encode(["kind": "host", "name": "Fixture host"])
                let body = try noise.writeMessage(intro)
                channels[key] = HostChannel(noise: noise, id: frame.channelID)
                handshakes += 1
                handshakeWaiters.forEach { $0.resume() }
                handshakeWaiters.removeAll()
                if replyToHandshake {
                    try hostFrame(kind: .msg2, channelID: frame.channelID, body: body)
                }
            case .msg3:
                let channel = try #require(channels[key])
                let intro = try JSONDecoder().decode(JSONValue.self, from: channel.noise.readMessage(frame.body))
                guard intro["kind"]?.string == "app" else { throw RelayError.invalidMessage }
            case .transport:
                let channel = try #require(channels[key])
                let json = try JSONDecoder().decode(JSONValue.self, from: channel.noise.decrypt(frame.body))
                let id = try Int(#require(json["id"]?.number))
                channel.requests.append(id)
                requestIDs.append(id)
                notifyRequestWaiters()
                switch mode {
                case .echo:
                    try reply(id, channel: channel)
                case .interleaved:
                    try envelope(["ev": .string("session.upsert"), "seq": .number(10),
                                  "data": .object(["id": .string("before-response")])], channel: channel)
                    try reply(id, channel: channel)
                    try envelope(["ev": .string("sessions.synced"), "seq": .number(11), "data": .object([:])], channel: channel)
                case .reverseTwo:
                    if channel.requests.count == 2 {
                        try reply(channel.requests[1], channel: channel)
                        try reply(channel.requests[0], channel: channel)
                    }
                case .silent: break
                case .tamper:
                    let response = try JSONEncoder().encode(JSONValue.object(["re": .number(Double(id)), "ok": .bool(true), "result": .object([:])]))
                    var ciphertext = try channel.noise.encrypt(response)
                    ciphertext[ciphertext.startIndex] ^= 1
                    try hostFrame(kind: .transport, channelID: frame.channelID, body: ciphertext)
                }
            case .close:
                closeFrames += 1
                channels.removeValue(forKey: key)
            default: throw RelayError.invalidMessage
            }
        }
    }

    func receive() async throws -> RelaySocketMessage {
        if !inbound.isEmpty {
            return inbound.removeFirst()
        }
        guard !isClosed else { throw RelayError.disconnected }
        return try await withCheckedThrowingContinuation { receivers.append($0) }
    }

    func ping() throws {
        if isClosed {
            throw RelayError.disconnected
        }
    }

    func close() {
        guard !isClosed else { return }
        isClosed = true
        for receiver in receivers {
            receiver.resume(throwing: RelayError.disconnected)
        }
        receivers.removeAll()
        for (_, waiter) in requestWaiters {
            waiter.resume(throwing: RelayError.disconnected)
        }
        requestWaiters.removeAll()
        for waiter in handshakeWaiters {
            waiter.resume(throwing: RelayError.disconnected)
        }
        handshakeWaiters.removeAll()
    }

    func waitForRequests(_ count: Int) async throws {
        guard requestIDs.count < count else { return }
        guard !isClosed else { throw RelayError.disconnected }
        try await withCheckedThrowingContinuation { requestWaiters.append((count, $0)) }
    }

    func waitForHandshake() async throws {
        guard handshakes == 0 else { return }
        guard !isClosed else { throw RelayError.disconnected }
        try await withCheckedThrowingContinuation { handshakeWaiters.append($0) }
    }

    private func notifyRequestWaiters() {
        let ready = requestWaiters.filter { $0.0 <= requestIDs.count }
        requestWaiters.removeAll { $0.0 <= requestIDs.count }
        for (_, waiter) in ready {
            waiter.resume()
        }
    }

    private func deliver(_ message: RelaySocketMessage) {
        if !receivers.isEmpty {
            receivers.removeFirst().resume(returning: message)
        } else {
            inbound.append(message)
        }
    }

    private func control(_ value: [String: JSONValue]) throws {
        let data = try JSONEncoder().encode(JSONValue.object(value))
        deliver(.text(String(decoding: data, as: UTF8.self)))
    }

    private func presence() throws {
        try control(["t": .string("presence"), "devices": .array([
            .object(["id": .string(hostKey.deviceID), "name": .string("Fixture host"), "kind": .string("host")]),
        ])])
    }

    private func hostFrame(kind: RelayWire.Kind, channelID: Data, body: Data) throws {
        try deliver(.data(RelayWire.frame(peerID: hostKey.deviceID, kind: kind, channelID: channelID, body: body)))
    }

    private func reply(_ id: Int, channel: HostChannel) throws {
        try envelope(["re": .number(Double(id)), "ok": .bool(true), "result": .object(["request": .number(Double(id))])], channel: channel)
    }

    private func envelope(_ fields: [String: JSONValue], channel: HostChannel) throws {
        let plaintext = try JSONEncoder().encode(JSONValue.object(fields))
        try hostFrame(kind: .transport, channelID: channel.id, body: channel.noise.encrypt(plaintext))
    }
}

@Test(.timeLimit(.minutes(1))) func relayAcceptsProvedPresenceBeforeReadyWithoutLosingIt() async throws {
    let socket = MockRelaySocket(presenceBeforeReady: true)
    let bridge = await socket.makeBridge()
    let events = try await bridge.connect()
    var iterator = events.makeAsyncIterator()
    let event = try #require(await iterator.next())
    guard case let .presence(machines) = event else { Issue.record("Expected presence"); return }
    #expect(machines.count == 1)
    #expect(await machines.first?.id == socket.hostID())
    #expect(machines.first?.online == true)
    #expect(await socket.proofAccepted)
    await bridge.disconnect()
    #expect(await socket.isClosed)
}

@Test(.timeLimit(.minutes(1))) func relayRejectsChallengeForAnotherAccountAndReadyForAnotherDevice() async throws {
    for socket in [MockRelaySocket(wrongAccount: true), MockRelaySocket(wrongReadyDevice: true)] {
        let bridge = await socket.makeBridge()
        await #expect(throws: RelayError.invalidMessage) { try await bridge.connect(timeout: .seconds(1)) }
        #expect(await socket.isClosed)
    }
}

@Test(.timeLimit(.minutes(1))) func relayHandshakeTimeoutClosesTheSocket() async throws {
    let socket = MockRelaySocket(ready: false)
    let bridge = await socket.makeBridge()
    await #expect(throws: RelayError.timeout) { try await bridge.connect(timeout: .milliseconds(100)) }
    #expect(await socket.isClosed)
}

@Test(.timeLimit(.minutes(1))) func relayEncryptedStreamKeepsEventsAndResponseInWireOrder() async throws {
    let socket = MockRelaySocket(mode: .interleaved)
    let bridge = await socket.makeBridge()
    let events = try await bridge.connect()
    let channel = try await bridge.open(peerID: socket.hostID())
    let collector = Task {
        var result: [BridgeEnvelope] = []
        for try await message in channel.messages {
            result.append(message)
            if result.count == 3 {
                break
            }
        }
        return result
    }
    let response = try await channel.request(BridgeRequest(id: 1, op: "sessions.subscribe"))
    #expect(response.re == 1)
    let messages = try await collector.value
    #expect(messages.map { $0.ev ?? "response" } == ["session.upsert", "response", "sessions.synced"])
    #expect(messages[1].re == 1)
    #expect(channel.peerKey == socket.hostKey.publicKey)
    #expect(channel.handshakeHash.count == 32)
    await channel.disconnect()
    await bridge.disconnect()
    withExtendedLifetime(events) {}
}

@Test(.timeLimit(.minutes(1))) func relayConcurrentRequestsMatchTheirIDsWhenResponsesArriveInReverseOrder() async throws {
    let socket = MockRelaySocket(mode: .reverseTwo)
    let bridge = await socket.makeBridge()
    let events = try await bridge.connect()
    let channel = try await bridge.open(peerID: socket.hostID())
    async let first = channel.request(BridgeRequest(id: 1, op: "hello"))
    async let second = channel.request(BridgeRequest(id: 2, op: "sessions.list"))
    let responses = try await (first, second)
    #expect(responses.0.re == 1)
    #expect(responses.1.re == 2)
    #expect(await Set(socket.requestIDs) == [1, 2])
    await bridge.disconnect()
    withExtendedLifetime(events) {}
}

@Test(.timeLimit(.minutes(1))) func relayConcurrentSendsRemainInNoiseNonceOrder() async throws {
    let socket = MockRelaySocket()
    let bridge = await socket.makeBridge()
    let events = try await bridge.connect()
    let channel = try await bridge.open(peerID: socket.hostID())
    try await withThrowingTaskGroup(of: Int.self) { group in
        for id in 1 ... 20 {
            group.addTask {
                let response = try await channel.request(BridgeRequest(id: id, op: "session.get"))
                return try #require(response.re)
            }
        }
        var responses = Set<Int>()
        for try await id in group {
            responses.insert(id)
        }
        #expect(responses == Set(1 ... 20))
    }
    #expect(await socket.requestIDs.count == 20)
    await bridge.disconnect()
    withExtendedLifetime(events) {}
}

@Test(.timeLimit(.minutes(1))) func relayCanonicalizesHostDeviceIDsBeforeMatchingHandshakeFrames() async throws {
    let socket = MockRelaySocket()
    let bridge = await socket.makeBridge()
    let events = try await bridge.connect()
    let channel = try await bridge.open(peerID: socket.hostID().uppercased(), timeout: .seconds(1))
    #expect(try await channel.request(BridgeRequest(id: 1, op: "hello")).ok == true)
    await bridge.disconnect()
    withExtendedLifetime(events) {}
}

@Test(.timeLimit(.minutes(1))) func relayRejectsAHostWhoseNoiseKeyDoesNotMatchItsDeviceID() async throws {
    let socket = MockRelaySocket(wrongHostKey: true)
    let bridge = await socket.makeBridge()
    let events = try await bridge.connect()
    await #expect(throws: NoiseFailure.peerMismatch) { try await bridge.open(peerID: socket.hostID()) }
    #expect(await socket.requestIDs.isEmpty)
    #expect(await !socket.isClosed)
    await bridge.disconnect()
    withExtendedLifetime(events) {}
}

@Test(.timeLimit(.minutes(1))) func relayChannelOpeningTimesOutWithoutDisconnectingOtherMachines() async throws {
    let socket = MockRelaySocket(replyToHandshake: false)
    let bridge = await socket.makeBridge()
    let events = try await bridge.connect()
    await #expect(throws: RelayError.timeout) { try await bridge.open(peerID: socket.hostID(), timeout: .milliseconds(100)) }
    #expect(await !socket.isClosed)
    await bridge.disconnect()
    withExtendedLifetime(events) {}
}

@Test(.timeLimit(.minutes(1))) func relayChannelOpeningCancellationDropsItsPendingHandshake() async throws {
    let socket = MockRelaySocket(replyToHandshake: false)
    let bridge = await socket.makeBridge()
    let events = try await bridge.connect()
    let opening = Task { try await bridge.open(peerID: socket.hostID()) }
    try await socket.waitForHandshake()
    opening.cancel()
    await #expect(throws: CancellationError.self) { try await opening.value }
    #expect(await !socket.isClosed)
    await bridge.disconnect()
    withExtendedLifetime(events) {}
}

@Test(.timeLimit(.minutes(1))) func relayRequestTimeoutClosesTheChannelWithoutReplayingTheRequest() async throws {
    let socket = MockRelaySocket(mode: .silent)
    let bridge = await socket.makeBridge()
    let events = try await bridge.connect()
    let channel = try await bridge.open(peerID: socket.hostID())
    await #expect(throws: RelayError.timeout) {
        try await channel.request(BridgeRequest(id: 1, op: "session.reply"), timeout: .milliseconds(100))
    }
    await #expect(throws: RelayError.disconnected) { try await channel.request(BridgeRequest(id: 2, op: "hello")) }
    let next = try await bridge.open(peerID: socket.hostID())
    #expect(await socket.requestIDs == [1])
    await next.disconnect()
    await bridge.disconnect()
    withExtendedLifetime(events) {}
}

@Test(.timeLimit(.minutes(1))) func relayRequestCancellationTearsDownOnlyThatChannel() async throws {
    let socket = MockRelaySocket(mode: .silent)
    let bridge = await socket.makeBridge()
    let events = try await bridge.connect()
    let channel = try await bridge.open(peerID: socket.hostID())
    let request = Task { try await channel.request(BridgeRequest(id: 1, op: "sessions.subscribe")) }
    try await socket.waitForRequests(1)
    request.cancel()
    await #expect(throws: CancellationError.self) { try await request.value }
    await #expect(throws: RelayError.disconnected) { try await channel.request(BridgeRequest(id: 2, op: "hello")) }
    #expect(await !socket.isClosed)
    #expect(await socket.requestIDs == [1])
    await bridge.disconnect()
    withExtendedLifetime(events) {}
}

@Test(.timeLimit(.minutes(1))) func relayTamperedCiphertextFailsThePendingRequestAndPoisonsTheChannel() async throws {
    let socket = MockRelaySocket(mode: .tamper)
    let bridge = await socket.makeBridge()
    let events = try await bridge.connect()
    let channel = try await bridge.open(peerID: socket.hostID())
    await #expect(throws: NoiseFailure.authenticationFailed) {
        try await channel.request(BridgeRequest(id: 1, op: "session.reply"))
    }
    await #expect(throws: RelayError.disconnected) { try await channel.request(BridgeRequest(id: 2, op: "hello")) }
    #expect(await socket.requestIDs == [1])
    #expect(await !socket.isClosed)
    await bridge.disconnect()
    withExtendedLifetime(events) {}
}
