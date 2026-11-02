#if os(macOS)
    import Foundation
    import Network

    /// One reader owns the wire order. Responses are also published so reducers can
    /// start a replacement snapshot at the subscribe response, before its first event.
    public actor LocalBridge {
        public enum Failure: LocalizedError, Sendable {
            case disconnected, timeout, duplicateID, invalidMessage, eventOverflow
            case connection(String)

            public var errorDescription: String? {
                switch self {
                case .disconnected: "The local host disconnected."
                case .timeout: "The local host did not respond in time."
                case .duplicateID: "A request identifier was reused."
                case .invalidMessage: "The local host sent an invalid message."
                case .eventOverflow: "Session updates arrived too quickly. Reconnect to sync again."
                case let .connection(reason): "Could not connect to the local host: \(reason)"
                }
            }
        }

        private var connection: NWConnection?
        private var generation = UUID()
        private var opening: CheckedContinuation<Void, any Error>?
        private var openTimeout: Task<Void, Never>?
        private var pending: [Int: CheckedContinuation<BridgeEnvelope, any Error>] = [:]
        private var timeouts: [Int: Task<Void, Never>] = [:]
        private var usedIDs: Set<Int> = []
        private var messages: AsyncThrowingStream<BridgeEnvelope, any Error>.Continuation?
        private var buffer = Data()
        private let queue = DispatchQueue(label: "dev.agentbridge.local")

        public init() {}

        public func connect(path: String, timeout: Duration = .seconds(5)) async throws
            -> AsyncThrowingStream<BridgeEnvelope, any Error>
        {
            disconnect()
            guard !path.isEmpty, !path.utf8.contains(0), path.utf8.count <= 103 else {
                throw Failure.connection("The socket path must fit in 103 UTF-8 bytes.")
            }
            let token = UUID()
            generation = token
            let stream = AsyncThrowingStream<BridgeEnvelope, any Error>.makeStream(
                bufferingPolicy: .bufferingOldest(512)
            )
            messages = stream.continuation
            messages?.onTermination = { @Sendable [weak self] _ in
                Task { await self?.finish(Failure.disconnected, generation: token) }
            }
            let socket = NWConnection(to: .unix(path: path), using: .tcp)
            connection = socket
            socket.stateUpdateHandler = { [weak self] state in
                Task { await self?.stateChanged(state, generation: token) }
            }
            do {
                try await withTaskCancellationHandler {
                    try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
                        opening = continuation
                        openTimeout = Task { [weak self] in
                            do { try await Task.sleep(for: timeout) } catch { return }
                            await self?.finish(Failure.timeout, generation: token)
                        }
                        socket.start(queue: queue)
                        if Task.isCancelled {
                            finish(CancellationError(), generation: token)
                        }
                    }
                } onCancel: {
                    Task { await self.finish(CancellationError(), generation: token) }
                }
                return stream.stream
            } catch {
                finish(error, generation: token)
                throw error
            }
        }

        public func request(_ request: BridgeRequest, timeout: Duration = .seconds(10)) async throws -> BridgeEnvelope {
            try Task.checkCancellation()
            guard let connection else { throw Failure.disconnected }
            guard request.id > 0, usedIDs.insert(request.id).inserted else { throw Failure.duplicateID }
            let frame = try LocalFraming.encode(JSONEncoder().encode(request))
            let token = generation
            return try await withTaskCancellationHandler {
                try await withCheckedThrowingContinuation { continuation in
                    pending[request.id] = continuation
                    timeouts[request.id] = Task { [weak self] in
                        do { try await Task.sleep(for: timeout) } catch { return }
                        await self?.finish(Failure.timeout, generation: token)
                    }
                    connection.send(content: frame, completion: .contentProcessed { [weak self] error in
                        if let error {
                            Task { await self?.finish(Failure.connection(error.localizedDescription), generation: token) }
                        }
                    })
                    if Task.isCancelled {
                        finish(CancellationError(), generation: token)
                    }
                }
            } onCancel: {
                // A cancelled subscribe can still change the host's feed; closing the
                // channel prevents an ambiguous snapshot boundary from leaking through.
                Task { await self.finish(CancellationError(), generation: token) }
            }
        }

        public func disconnect() {
            finish(Failure.disconnected, generation: generation)
        }

        private func stateChanged(_ state: NWConnection.State, generation token: UUID) {
            guard token == generation else { return }
            switch state {
            case .ready:
                openTimeout?.cancel()
                openTimeout = nil
                opening?.resume()
                opening = nil
                receive(generation: token)
            case let .failed(error), let .waiting(error):
                finish(Failure.connection(error.localizedDescription), generation: token)
            case .cancelled:
                finish(Failure.disconnected, generation: token)
            default: break
            }
        }

        private func receive(generation token: UUID) {
            connection?.receive(minimumIncompleteLength: 1, maximumLength: 65523) { [weak self] data, _, complete, error in
                Task { await self?.received(data, complete: complete, error: error, generation: token) }
            }
        }

        private func received(_ data: Data?, complete: Bool, error: NWError?, generation token: UUID) {
            guard token == generation, connection != nil else { return }
            do {
                if let data {
                    buffer.append(data)
                }
                while buffer.count >= 4 {
                    let count = buffer.prefix(4).enumerated().reduce(0) { $0 | (Int($1.element) << ($1.offset * 8)) }
                    guard count <= LocalFraming.maximumPayloadSize else { throw LocalFraming.FrameError.oversized(count) }
                    guard buffer.count >= count + 4 else { break }
                    let payload = buffer.subdata(in: 4 ..< (4 + count))
                    buffer = Data(buffer.dropFirst(4 + count))
                    let envelope = try JSONDecoder().decode(BridgeEnvelope.self, from: payload)
                    let isResponse = envelope.re != nil && envelope.ok != nil && envelope.ev == nil
                    let isEvent = envelope.ev != nil && envelope.seq != nil && envelope.re == nil
                    guard isResponse || isEvent else { throw Failure.invalidMessage }
                    if isResponse, pending[envelope.re!] == nil {
                        throw Failure.invalidMessage
                    }
                    if case .dropped = messages?.yield(envelope) {
                        throw Failure.eventOverflow
                    }
                    if let id = envelope.re {
                        timeouts.removeValue(forKey: id)?.cancel()
                        pending.removeValue(forKey: id)?.resume(returning: envelope)
                    }
                }
                if let error {
                    throw Failure.connection(error.localizedDescription)
                }
                if complete {
                    throw buffer.isEmpty ? Failure.disconnected : Failure.invalidMessage
                }
                receive(generation: token)
            } catch { finish(error, generation: token) }
        }

        private func finish(_ error: any Error, generation token: UUID) {
            guard token == generation else { return }
            generation = UUID()
            connection?.stateUpdateHandler = nil
            connection?.cancel()
            connection = nil
            openTimeout?.cancel()
            openTimeout = nil
            opening?.resume(throwing: error)
            opening = nil
            for timer in timeouts.values {
                timer.cancel()
            }
            timeouts.removeAll()
            for continuation in pending.values {
                continuation.resume(throwing: error)
            }
            pending.removeAll()
            usedIDs.removeAll()
            messages?.finish(throwing: error)
            messages = nil
            buffer.removeAll()
        }
    }
#endif
