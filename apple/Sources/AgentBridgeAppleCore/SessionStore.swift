import Foundation

/// Applies the snapshot-and-event rules of Bridge Protocol v1.
/// A fresh subscription starts a replacement snapshot, not an incremental merge.
public struct SessionStore: Sendable {
    public private(set) var sessions: [String: BridgeSession] = [:]
    public private(set) var lastSequence: Int?
    private var snapshot: [String: BridgeSession]?

    public init() {}

    public mutating func beginSubscription() {
        snapshot = [:]
        lastSequence = nil
    }

    /// Returns false if a sequence gap requires resubscription.
    @discardableResult
    public mutating func receive(_ envelope: BridgeEnvelope) -> Bool {
        if let seq = envelope.seq {
            if let lastSequence, seq != lastSequence + 1 {
                snapshot = nil
                return false
            }
            lastSequence = seq
        }
        switch envelope.ev {
        case "session.upsert":
            guard let data = envelope.data, let session = BridgeSession(json: data) else { return true }
            if snapshot != nil { snapshot?[session.id] = session }
            else { sessions[session.id] = session }
        case "session.remove":
            guard let id = envelope.data?["id"]?.string else { return true }
            if snapshot != nil { snapshot?.removeValue(forKey: id) }
            else { sessions.removeValue(forKey: id) }
        case "sessions.synced":
            if let snapshot { sessions = snapshot }
            snapshot = nil
        default:
            break
        }
        return true
    }

    public var orderedSessions: [BridgeSession] {
        sessions.values.sorted { $0.id < $1.id }
    }
}
