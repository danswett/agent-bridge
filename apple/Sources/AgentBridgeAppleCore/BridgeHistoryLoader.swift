import Foundation
import Observation

/// Keeps one request in flight and one latest refresh pending. Hiding a pane
/// invalidates its result without cancelling a shared host connection.
@MainActor @Observable
public final class BridgeHistoryLoader {
    public private(set) var value: JSONValue?
    public private(set) var isLoading = false
    public private(set) var hasLoaded = false
    public private(set) var error: String?
    public private(set) var loadedAt: Date?

    @ObservationIgnored private var task: Task<Void, Never>?
    @ObservationIgnored private var pending: (@MainActor () async throws -> JSONValue)?
    @ObservationIgnored private var generation = UUID()
    @ObservationIgnored private var sessionID: String?
    @ObservationIgnored private var active = false

    public init() {}

    public func activate(sessionID: String) {
        generation = UUID()
        active = true
        if self.sessionID != sessionID {
            value = nil; error = nil; loadedAt = nil; hasLoaded = false
        }
        self.sessionID = sessionID
    }

    public func deactivate() {
        active = false
        generation = UUID()
        pending = nil
    }

    public func refresh(load: @escaping @MainActor (String) async throws -> JSONValue) {
        guard active, let sessionID else { return }
        pending = { try await load(sessionID) }
        guard task == nil else { return }
        isLoading = true
        task = Task { await drain() }
    }

    private func drain() async {
        while active, let request = pending {
            pending = nil
            let token = generation
            let expected = sessionID
            error = nil
            do {
                let response = try await request()
                guard active, token == generation else { continue }
                if response["missing"]?.bool == true {
                    value = nil
                    error = "This session has ended."
                } else if response["id"]?.string != expected {
                    error = "The host returned details for a different session."
                } else {
                    value = response
                    loadedAt = .now
                }
                hasLoaded = true
            } catch {
                guard active, token == generation else { continue }
                self.error = "Couldn’t load tool history: \(error.localizedDescription)"
            }
        }
        isLoading = false
        task = nil
    }
}
