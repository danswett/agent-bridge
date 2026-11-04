import Foundation

/// The host fetches and verifies a blob asynchronously. Repeating the same start
/// observes that work; a new attachment identity would start a second fetch.
public enum BridgeBlobStaging {
    public static func confirm(_ attachment: BridgeBlobAttachment, ticket: String, key: Data, capabilities: Set<String>,
                               timeout: Duration = .seconds(20 * 60), pollInterval: Duration = .milliseconds(400),
                               validateConnection: @escaping @Sendable () async throws -> Void,
                               send: @escaping @Sendable (BridgeOperation, Duration) async throws -> JSONValue,
                               deleteBlob: @escaping @Sendable () async throws -> Void) async throws -> UUID
    {
        do {
            let start = try attachment.start(ticket: ticket, key: key, capabilities: capabilities, isLocal: false)
            let clock = ContinuousClock()
            let deadline = clock.now.advanced(by: timeout)
            var received = 0
            while true {
                try Task.checkCancellation()
                try await validateConnection()
                try Task.checkCancellation()
                let remaining = clock.now.duration(to: deadline)
                guard remaining > .zero else { throw RelayError.timeout }
                // Cancelling this UI operation must not cancel the shared Noise
                // request and tear down every session using its channel.
                let result = try await Task { try await send(start, min(.seconds(120), remaining)) }.value
                try Task.checkCancellation()
                try await validateConnection()
                try Task.checkCancellation()
                guard clock.now < deadline else { throw RelayError.timeout }
                let state = try BridgeAttachmentState(json: result)
                guard state.received >= received, state.received <= attachment.size,
                      !state.complete || state.received == attachment.size
                else {
                    throw BridgeOperationError("The machine returned an invalid blob attachment offset.")
                }
                if state.complete {
                    return attachment.id
                }
                received = state.received
                // All bytes may be present while the host still checks the digest.
                try await Task.sleep(for: min(max(.milliseconds(1), pollInterval), clock.now.duration(to: deadline)))
            }
        } catch {
            // The cancelled caller cannot send cleanup: both transports reject it
            // before writing. Keep cleanup independent, bounded and on the old peer.
            await Task {
                do {
                    try await validateConnection()
                    _ = try await send(.init(op: "attachment.discard", data: ["attachment": .string(attachment.id.uuidString.lowercased())]), .seconds(5))
                } catch {}
                try? await deleteBlob()
            }.value
            throw error
        }
    }
}
