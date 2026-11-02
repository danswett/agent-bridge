import Foundation
import Security

public struct RelayMachine: Identifiable, Sendable, Equatable {
    public let id: String
    public let name: String
    public let kind: String
    public let lastSeen: String?
    public let online: Bool
}

public enum RelayEvent: Sendable {
    case presence([RelayMachine])
}

enum RelaySocketMessage: Sendable { case text(String), data(Data) }

protocol RelaySocket: Sendable {
    func send(_ message: RelaySocketMessage) async throws
    func receive() async throws -> RelaySocketMessage
    func ping() async throws
    func close() async
}

private actor FoundationRelaySocket: RelaySocket {
    let session: URLSession
    let task: URLSessionWebSocketTask

    init(url: URL) {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 30
        session = URLSession(configuration: config, delegate: NoRedirects(), delegateQueue: nil)
        task = session.webSocketTask(with: url)
        task.maximumMessageSize = 65561
        task.resume()
    }

    func send(_ message: RelaySocketMessage) async throws {
        switch message {
        case let .text(text): try await task.send(.string(text))
        case let .data(data): try await task.send(.data(data))
        }
    }

    func receive() async throws -> RelaySocketMessage {
        switch try await task.receive() {
        case let .string(text): .text(text)
        case let .data(data): .data(data)
        @unknown default: throw RelayError.invalidMessage
        }
    }

    func ping() async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
            task.sendPing { error in
                if let error {
                    continuation.resume(throwing: error)
                } else {
                    continuation.resume()
                }
            }
        }
    }

    func close() {
        task.cancel(with: .goingAway, reason: nil)
        session.invalidateAndCancel()
    }
}

/// A channel is bound to one verified peer and one relay connection. It is never
/// reused across reconnects, and a mutation is never replayed by this transport.
public struct RelayChannel: Sendable {
    public let messages: AsyncThrowingStream<BridgeEnvelope, any Error>
    public let peerKey: Data
    public let handshakeHash: Data
    private let owner: RelayBridge
    private let key: String

    fileprivate init(messages: AsyncThrowingStream<BridgeEnvelope, any Error>, peerKey: Data,
                     handshakeHash: Data, owner: RelayBridge, key: String)
    {
        self.messages = messages
        self.peerKey = peerKey
        self.handshakeHash = handshakeHash
        self.owner = owner
        self.key = key
    }

    public func request(_ request: BridgeRequest, timeout: Duration = .seconds(15)) async throws -> BridgeEnvelope {
        try await owner.request(request, channel: key, timeout: timeout)
    }

    public func disconnect() async {
        await owner.closeChannel(key, error: RelayError.disconnected)
    }
}

/// Only this actor owns Noise state. Encrypting and enqueuing a frame is atomic;
/// the single outbound chain writes frames in exactly that nonce order.
public actor RelayBridge {
    private final class Channel {
        let peerID: String
        let id: Data
        var noise: NoiseXXHandshake
        let stream = AsyncThrowingStream<BridgeEnvelope, any Error>.makeStream(bufferingPolicy: .bufferingOldest(512))
        var opening: CheckedContinuation<RelayChannel, any Error>?
        var pending: [Int: CheckedContinuation<BridgeEnvelope, any Error>] = [:]
        var timeouts: [Int: Task<Void, Never>] = [:]
        var usedIDs = Set<Int>()
        var deadline: Task<Void, Never>?

        init(peerID: String, id: Data, noise: NoiseXXHandshake) {
            self.peerID = peerID
            self.id = id
            self.noise = noise
        }
    }

    private let socket: any RelaySocket
    private let account: RelayAccount
    private let identity: NoiseKeyPair
    private let name: String
    private let kind: String
    private var started = false
    private var authenticated = false
    private var proved = false
    private var closed = false
    private var channels: [String: Channel] = [:]
    private var events: AsyncThrowingStream<RelayEvent, any Error>.Continuation?
    private var opening: CheckedContinuation<Void, any Error>?
    private var receiveTask: Task<Void, Never>?
    private var heartbeat: Task<Void, Never>?
    private var deadline: Task<Void, Never>?
    private var sendTail: Task<Void, any Error>?
    private var queuedSends = 0
    public private(set) var blobMaximum = 0
    private var nextControlID = 0
    private var controlRequests: [Int: CheckedContinuation<JSONValue, any Error>] = [:]
    private var controlTypes: [Int: String] = [:]
    private var controlTimeouts: [Int: Task<Void, Never>] = [:]

    public init(relay: String, account: RelayAccount, identity: NoiseKeyPair, name: String, kind: String) throws {
        let base = try RelayAddress.validated(relay)
        var parts = URLComponents(url: base, resolvingAgainstBaseURL: false)!
        parts.scheme = "wss"
        parts.path = "/v1/ws"
        socket = FoundationRelaySocket(url: parts.url!)
        self.account = account
        self.identity = identity
        self.name = String(name.prefix(80))
        self.kind = kind == "phone" ? "phone" : "app"
    }

    init(socket: any RelaySocket, account: RelayAccount, identity: NoiseKeyPair, name: String = "Test", kind: String = "app") {
        self.socket = socket
        self.account = account
        self.identity = identity
        self.name = name
        self.kind = kind
    }

    public func connect(timeout: Duration = .seconds(20)) async throws -> AsyncThrowingStream<RelayEvent, any Error> {
        guard !started, !closed else { throw RelayError.disconnected }
        started = true
        let stream = AsyncThrowingStream<RelayEvent, any Error>.makeStream(bufferingPolicy: .bufferingOldest(64))
        events = stream.continuation
        events?.onTermination = { @Sendable [weak self] _ in Task { await self?.disconnect() } }
        receiveTask = Task { await readLoop() }
        deadline = Task {
            do { try await Task.sleep(for: timeout) } catch { return }
            await finish(RelayError.timeout)
        }
        do {
            try await withTaskCancellationHandler {
                try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
                    opening = continuation
                    enqueueControl(.object(["t": .string("hello"), "token": .string(account.token),
                                            "pub": .string(RelayWire.hex(identity.publicKey)),
                                            "name": .string(name), "kind": .string(kind)]))
                    if Task.isCancelled {
                        Task { await finish(CancellationError()) }
                    }
                }
            } onCancel: { Task { await self.disconnect() } }
            return stream.stream
        } catch {
            await finish(error)
            throw error
        }
    }

    public func open(peerID: String, timeout: Duration = .seconds(20)) async throws -> RelayChannel {
        try Task.checkCancellation()
        let peerID = peerID.lowercased()
        guard authenticated, !closed, channels.count < 64 else { throw RelayError.disconnected }
        var bytes = [UInt8](repeating: 0, count: 8)
        guard SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) == errSecSuccess else { throw RelayError.invalidMessage }
        let id = Data(bytes)
        let key = peerID + ":" + RelayWire.hex(id)
        let prologue = try RelayWire.prologue(initiator: identity.deviceID, responder: peerID, channelID: id)
        let noise = try NoiseXXHandshake(role: .initiator, staticKey: identity, prologue: prologue, expectedPeerID: peerID)
        let channel = Channel(peerID: peerID, id: id, noise: noise)
        channels[key] = channel
        channel.stream.continuation.onTermination = { @Sendable [weak self] _ in
            Task { await self?.closeChannel(key, error: RelayError.disconnected) }
        }
        let message = try channel.noise.writeMessage()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                channel.opening = continuation
                channel.deadline = Task {
                    do { try await Task.sleep(for: timeout) } catch { return }
                    closeChannel(key, error: RelayError.timeout)
                }
                enqueueFrame(channel, kind: .msg1, body: message)
                if Task.isCancelled {
                    closeChannel(key, error: CancellationError())
                }
            }
        } onCancel: { Task { await self.closeChannel(key, error: CancellationError()) } }
    }

    fileprivate func request(_ request: BridgeRequest, channel key: String, timeout: Duration) async throws -> BridgeEnvelope {
        try Task.checkCancellation()
        guard !closed, let channel = channels[key], channel.noise.isComplete else { throw RelayError.disconnected }
        guard request.id > 0, channel.usedIDs.insert(request.id).inserted else { throw RelayError.invalidMessage }
        let payload = try JSONEncoder().encode(request)
        guard payload.count <= 65519 else { throw RelayError.invalidMessage }
        // No suspension between encryption and enqueuing: nonce order equals wire order.
        let encrypted = try channel.noise.encrypt(payload)
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                channel.pending[request.id] = continuation
                channel.timeouts[request.id] = Task {
                    do { try await Task.sleep(for: timeout) } catch { return }
                    closeChannel(key, error: RelayError.timeout)
                }
                enqueueFrame(channel, kind: .transport, body: encrypted)
                if Task.isCancelled {
                    closeChannel(key, error: CancellationError())
                }
            }
        } onCancel: { Task { await self.closeChannel(key, error: CancellationError()) } }
    }

    public func disconnect() async {
        await finish(RelayError.disconnected)
    }

    public func blobPut(recipient: String, plaintextSize: Int) async throws -> JSONValue {
        try await control(expected: "blob-result") { id in
            try BridgeBlob.putMessage(id: id, recipient: recipient, plaintextSize: plaintextSize, storedLimit: self.blobMaximum)
        }
    }

    public func blobDelete(ticket: String, timeout: Duration = .seconds(30)) async throws {
        _ = try await control(expected: "blob-result", timeout: timeout) { try BridgeBlob.deleteMessage(id: $0, ticket: ticket) }
    }

    public func forget(machineID: String) async throws {
        guard RelayWire.isDeviceID(machineID) else { throw RelayError.invalidMessage }
        _ = try await control(expected: "forget-result") { id in
            .object(["t": .string("forget"), "id": .number(Double(id)), "machine": .string(machineID)])
        }
    }

    private func control(expected: String, timeout: Duration = .seconds(30), message: (Int) throws -> JSONValue) async throws -> JSONValue {
        try Task.checkCancellation()
        guard authenticated, !closed, controlRequests.count < 64 else { throw RelayError.disconnected }
        nextControlID += 1
        let id = nextControlID
        let body = try message(id)
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                controlRequests[id] = continuation
                controlTypes[id] = expected
                controlTimeouts[id] = Task {
                    do { try await Task.sleep(for: timeout) } catch { return }
                    failControl(id, error: RelayError.timeout)
                }
                enqueueControl(body)
                if Task.isCancelled {
                    failControl(id, error: CancellationError())
                }
            }
        } onCancel: { Task { await self.failControl(id, error: CancellationError()) } }
    }

    private func failControl(_ id: Int, error: any Error) {
        controlTypes.removeValue(forKey: id)
        controlTimeouts.removeValue(forKey: id)?.cancel()
        controlRequests.removeValue(forKey: id)?.resume(throwing: error)
    }

    fileprivate func closeChannel(_ key: String, error: any Error) {
        guard let channel = channels.removeValue(forKey: key) else { return }
        if !closed {
            enqueueFrame(channel, kind: .close, body: Data())
        }
        channel.deadline?.cancel()
        channel.opening?.resume(throwing: error)
        channel.opening = nil
        channel.timeouts.values.forEach { $0.cancel() }
        channel.pending.values.forEach { $0.resume(throwing: error) }
        channel.pending.removeAll()
        channel.stream.continuation.finish(throwing: error)
    }

    private func enqueueControl(_ json: JSONValue) {
        do {
            let data = try JSONEncoder().encode(json)
            guard data.count <= 4096, let string = String(data: data, encoding: .utf8) else { throw RelayError.invalidMessage }
            enqueue(.text(string))
        } catch { Task { await finish(error) } }
    }

    private func enqueueFrame(_ channel: Channel, kind: RelayWire.Kind, body: Data) {
        do { try enqueue(.data(RelayWire.frame(peerID: channel.peerID, kind: kind, channelID: channel.id, body: body))) }
        catch { Task { await finish(error) } }
    }

    private func enqueue(_ message: RelaySocketMessage) {
        guard !closed else { return }
        guard queuedSends < 256 else { Task { await finish(RelayError.overflow) }; return }
        queuedSends += 1
        let previous = sendTail
        let socket = socket
        let next = Task {
            try await previous?.value
            try Task.checkCancellation()
            try await socket.send(message)
            // Stay below the relay's burst limit, including attachment chunks.
            try await Task.sleep(for: .milliseconds(10))
        }
        sendTail = next
        Task {
            do { try await next.value; queuedSends -= 1 }
            catch { await finish(error) }
        }
    }

    private func readLoop() async {
        do {
            while !closed {
                let message = try await socket.receive()
                guard !closed else { return }
                switch message {
                case let .text(text): try receiveControl(text)
                case let .data(data): try receiveFrame(data)
                }
            }
        } catch { await finish(error) }
    }

    private func receiveControl(_ text: String) throws {
        guard text.utf8.count <= 4096 else { throw RelayError.invalidMessage }
        let json = try JSONDecoder().decode(JSONValue.self, from: Data(text.utf8))
        switch json["t"]?.string {
        case "challenge":
            guard !proved, !authenticated, let eph = json["eph"]?.string,
                  let nonce = json["nonce"]?.string, json["account"]?.string == account.accountId else { throw RelayError.invalidMessage }
            let secret = try identity.sharedSecret(with: RelayWire.unhex(eph))
            let mac = try RelayWire.proofMAC(sharedSecret: secret, nonce: RelayWire.unhex(nonce), accountID: account.accountId)
            proved = true
            enqueueControl(.object(["t": .string("proof"), "mac": .string(RelayWire.hex(mac))]))
        case "ready":
            guard proved, !authenticated, json["device"]?.string == identity.deviceID else { throw RelayError.invalidMessage }
            authenticated = true
            if let maximum = json["blobs"]?["max"]?.number, maximum.isFinite, maximum > 0, maximum <= 1_073_741_824 {
                blobMaximum = Int(maximum)
            }
            deadline?.cancel()
            deadline = nil
            opening?.resume()
            opening = nil
            heartbeat = Task { await keepAlive() }
        case "presence":
            guard proved, let devices = json["devices"]?.array else { throw RelayError.invalidMessage }
            let online = Set(devices.compactMap { $0["id"]?.string })
            var seen = Set<String>()
            let machines = try (devices + (json["known"]?.array ?? [])).compactMap { value -> RelayMachine? in
                guard let id = value["id"]?.string, let name = value["name"]?.string, let kind = value["kind"]?.string,
                      id.count == 32, (try? RelayWire.unhex(id).count) == 16 else { throw RelayError.invalidMessage }
                guard kind == "host", id != identity.deviceID, seen.insert(id).inserted else { return nil }
                return RelayMachine(id: id, name: name, kind: kind, lastSeen: value["lastSeen"]?.string ?? value["since"]?.string, online: online.contains(id))
            }
            for key in Array(channels.keys) where !online.contains(channels[key]!.peerID) {
                closeChannel(key, error: RelayError.disconnected)
            }
            if case .dropped = events?.yield(.presence(machines)) {
                throw RelayError.overflow
            }
        case "undeliverable":
            if let peerID = json["to"]?.string {
                for key in Array(channels.keys) where channels[key]?.peerID == peerID {
                    closeChannel(key, error: RelayError.disconnected)
                }
            }
        case "error": throw RelayError.refused("The relay refused the connection. Sign in again if your session expired.")
        case "blob-result", "forget-result":
            guard let rawID = json["id"]?.number, rawID > 0, rawID <= Double(Int32.max), rawID.rounded() == rawID else { throw RelayError.invalidMessage }
            let id = Int(rawID)
            guard let expected = controlTypes[id] else { return }
            guard expected == json["t"]?.string else { throw RelayError.invalidMessage }
            if json["error"]?.string != nil {
                failControl(id, error: RelayError.refused("The relay refused this request."))
            } else {
                controlTypes.removeValue(forKey: id)
                controlTimeouts.removeValue(forKey: id)?.cancel()
                controlRequests.removeValue(forKey: id)?.resume(returning: json)
            }
        default: break // New control types must not change authenticated channel state.
        }
    }

    private func receiveFrame(_ data: Data) throws {
        let frame = try RelayWire.parseFrame(data)
        let key = frame.peerID + ":" + RelayWire.hex(frame.channelID)
        guard authenticated, let channel = channels[key] else { return } // Client only initiates channels.
        do {
            switch frame.kind {
            case .msg2:
                guard !channel.noise.isComplete, channel.opening != nil else { throw RelayError.invalidMessage }
                let intro = try JSONDecoder().decode(JSONValue.self, from: channel.noise.readMessage(frame.body))
                guard intro["kind"]?.string == "host" else { throw RelayError.invalidMessage }
                let own = try JSONEncoder().encode(["name": name, "kind": kind])
                try enqueueFrame(channel, kind: .msg3, body: channel.noise.writeMessage(own))
                guard let peerKey = channel.noise.remoteStaticKey else { throw RelayError.invalidMessage }
                channel.deadline?.cancel()
                channel.opening?.resume(returning: RelayChannel(messages: channel.stream.stream, peerKey: peerKey,
                                                                handshakeHash: channel.noise.handshakeHash, owner: self, key: key))
                channel.opening = nil
            case .transport:
                guard channel.noise.isComplete else { throw RelayError.invalidMessage }
                let envelope = try JSONDecoder().decode(BridgeEnvelope.self, from: channel.noise.decrypt(frame.body))
                let response = envelope.re != nil && envelope.ok != nil && envelope.ev == nil
                let event = envelope.ev != nil && envelope.seq != nil && envelope.re == nil
                guard response || event else { throw RelayError.invalidMessage }
                if let id = envelope.re, channel.pending[id] == nil {
                    throw RelayError.invalidMessage
                }
                if case .dropped = channel.stream.continuation.yield(envelope) {
                    throw RelayError.overflow
                }
                if let id = envelope.re {
                    channel.timeouts.removeValue(forKey: id)?.cancel()
                    channel.pending.removeValue(forKey: id)?.resume(returning: envelope)
                }
            case .close: closeChannel(key, error: RelayError.disconnected)
            default: throw RelayError.invalidMessage
            }
        } catch { closeChannel(key, error: error) }
    }

    private func keepAlive() async {
        do {
            while !closed {
                try await Task.sleep(for: .seconds(20))
                let timeout = Task {
                    do { try await Task.sleep(for: .seconds(10)) } catch { return }
                    await finish(RelayError.timeout)
                }
                do { try await socket.ping(); timeout.cancel() }
                catch { timeout.cancel(); throw error }
            }
        } catch {
            if !Task.isCancelled {
                await finish(error)
            }
        }
    }

    private func finish(_ error: any Error) async {
        guard !closed else { return }
        closed = true
        authenticated = false
        receiveTask?.cancel()
        heartbeat?.cancel()
        deadline?.cancel()
        sendTail?.cancel()
        opening?.resume(throwing: error)
        opening = nil
        for key in Array(channels.keys) {
            closeChannel(key, error: error)
        }
        for id in Array(controlRequests.keys) {
            failControl(id, error: error)
        }
        events?.finish(throwing: error)
        events = nil
        await socket.close()
    }
}
