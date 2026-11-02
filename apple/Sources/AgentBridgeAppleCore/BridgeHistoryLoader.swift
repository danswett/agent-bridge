import Foundation
import Observation

/// Keeps one request in flight and one latest refresh pending. Hiding a pane
/// invalidates its result without cancelling a shared host connection.
@MainActor @Observable
public final class BridgeHistoryLoader {
    public enum Subject: Sendable { case session, delegate }
    public private(set) var value: JSONValue?
    public private(set) var isLoading = false
    public private(set) var hasLoaded = false
    public private(set) var error: String?
    public private(set) var loadedAt: Date?
    public private(set) var isMissing = false
    public var shouldRetry: Bool {
        error != nil && !isMissing
    }

    @ObservationIgnored private var task: Task<Void, Never>?
    private struct Pending {
        let load: @MainActor () async throws -> JSONValue
        let onValue: @MainActor (JSONValue) -> Void
    }

    @ObservationIgnored private var pending: Pending?
    @ObservationIgnored private let subject: Subject
    @ObservationIgnored private var generation = UUID()
    @ObservationIgnored private var sessionID: String?
    @ObservationIgnored private var active = false

    public init(subject: Subject = .session) {
        self.subject = subject
    }

    public func activate(sessionID: String) {
        generation = UUID()
        active = true
        if self.sessionID != sessionID {
            value = nil; error = nil; loadedAt = nil; hasLoaded = false; isMissing = false
        }
        self.sessionID = sessionID
    }

    public func deactivate() {
        active = false
        generation = UUID()
        pending = nil
    }

    public func refresh(load: @escaping @MainActor (String) async throws -> JSONValue) {
        refresh(load: load, onValue: { _ in })
    }

    public func refresh(load: @escaping @MainActor (String) async throws -> JSONValue,
                        onValue: @escaping @MainActor (JSONValue) -> Void)
    {
        guard active, let sessionID else { return }
        pending = Pending(load: { try await load(sessionID) }, onValue: onValue)
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
            isMissing = false
            do {
                let response = try await request.load()
                guard active, token == generation else { continue }
                if response["missing"]?.bool == true {
                    value = nil
                    isMissing = true
                    error = subject == .session ? "This session has ended." : "This delegate is no longer available."
                    request.onValue(response)
                } else if response["id"]?.string != expected {
                    error = subject == .session ? "The host returned details for a different session." : "The host returned details for a different delegate."
                } else {
                    value = response
                    loadedAt = .now
                    request.onValue(response)
                }
                hasLoaded = true
            } catch {
                guard active, token == generation else { continue }
                self.error = "Couldn’t load \(subject == .session ? "tool history" : "delegate details"): \(error.localizedDescription)"
            }
        }
        isLoading = false
        task = nil
    }
}
