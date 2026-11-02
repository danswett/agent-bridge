import Foundation

/// Enqueue scope changes synchronously where the account changes, then await the
/// returned task. A caller-created Task must not decide account intent later.
@MainActor
public final class BridgeUsageLifecycle {
    private struct Scope: Equatable {
        let relay: String
        let account: String
    }

    private let activateCache: @Sendable (String, String) async throws -> BridgeUsageCacheSession
    private let clearCache: @Sendable (String, String) async throws -> Void
    private var desired: Scope?
    private var generation = UUID()
    private var tail: Task<Void, Never>?

    public init(cache: BridgeUsageCache) {
        activateCache = { try await cache.activate(relay: $0, account: $1) }
        clearCache = { try await cache.clearScope(relay: $0, account: $1) }
    }

    /// A delayed backend makes lifecycle ordering deterministic in isolated tests.
    init(activate: @escaping @Sendable (String, String) async throws -> BridgeUsageCacheSession,
         clear: @escaping @Sendable (String, String) async throws -> Void)
    {
        activateCache = activate
        clearCache = clear
    }

    /// Obsolete requests return nil, including one already opening the disk cache.
    /// The returned operation is independent of cancellation of its awaiting view.
    public func activate(relay: String, account: String) -> Task<BridgeUsageCacheSession?, any Error> {
        let scope = Scope(relay: relay, account: account)
        desired = scope
        generation = UUID()
        let token = generation
        let previous = tail
        let operation = Task { @MainActor [self] in
            await previous?.value
            guard generation == token, desired == scope else { return nil as BridgeUsageCacheSession? }
            do {
                let session = try await activateCache(relay, account)
                guard generation == token, desired == scope else { return nil }
                return session
            } catch {
                guard generation == token, desired == scope else { return nil }
                throw error
            }
        }
        tail = Task { _ = try? await operation.value }
        return operation
    }

    /// Always deletes the named scope after older activations finish. Clearing a
    /// previous account does not cancel the newly desired account's activation.
    public func clear(relay: String, account: String) -> Task<Void, any Error> {
        if desired == Scope(relay: relay, account: account) {
            deactivate()
        }
        let previous = tail
        let clear = clearCache
        let operation = Task {
            await previous?.value
            try await clear(relay, account)
        }
        tail = Task { _ = try? await operation.value }
        return operation
    }

    /// Stop pending activation without deleting saved readings (for a signed-out,
    /// invalid, or sample connection state). Explicit sign-out also calls clear.
    public func deactivate() {
        generation = UUID()
        desired = nil
    }
}
