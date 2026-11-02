import Foundation

/// Receives every envelope in wire order, including subscription responses.
public struct SessionStore: Sendable {
    public private(set) var sessions: [String: BridgeSession] = [:]
    public private(set) var lastSequence: Int?
    public private(set) var isSynchronized = false
    public private(set) var discoveryHold: String?
    private var order: [String] = []
    private var snapshot: [String: BridgeSession]?
    private var snapshotOrder: [String] = []
    private var subscriptionID: Int?
    private var waitingForSnapshot = true

    public init() {}

    public mutating func expectSubscription(_ requestID: Int) {
        subscriptionID = requestID
        isSynchronized = false
    }

    /// Returns false if a sequence gap requires resubscription.
    @discardableResult
    public mutating func receive(_ envelope: BridgeEnvelope) -> Bool {
        if let id = envelope.re, id == subscriptionID {
            subscriptionID = nil
            if envelope.ok == true {
                snapshot = [:]
                snapshotOrder = []
                waitingForSnapshot = false
            }
            return true
        }
        guard envelope.ev != nil else { return true }
        if let seq = envelope.seq {
            let contiguous = lastSequence.map { $0 < Int.max && seq == $0 + 1 } ?? true
            lastSequence = seq
            if !contiguous {
                snapshot = nil
                snapshotOrder = []
                isSynchronized = false
                let needsRequest = !waitingForSnapshot && subscriptionID == nil
                waitingForSnapshot = true
                return !needsRequest
            }
        }
        guard !waitingForSnapshot else { return true }
        switch envelope.ev {
        case "session.upsert":
            guard let data = envelope.data, let session = BridgeSession(json: data) else { return true }
            if snapshot != nil {
                if snapshot?[session.id] == nil {
                    snapshotOrder.append(session.id)
                }
                snapshot?[session.id] = session
            } else {
                if sessions[session.id] == nil {
                    order.append(session.id)
                }
                sessions[session.id] = session
            }
        case "session.remove":
            guard let id = envelope.data?["id"]?.string else { return true }
            if snapshot != nil {
                snapshot?.removeValue(forKey: id)
                snapshotOrder.removeAll { $0 == id }
            } else {
                sessions.removeValue(forKey: id)
                order.removeAll { $0 == id }
            }
        case "sessions.synced":
            guard let snapshot else { return true }
            // Updates and refreshes preserve the person's place in the list.
            order = order.filter { snapshot[$0] != nil } + snapshotOrder.filter { !order.contains($0) }
            sessions = snapshot
            self.snapshot = nil
            snapshotOrder = []
            isSynchronized = true
            discoveryHold = envelope.data?["discoveryHeld"]?["reason"]?.string
        case "discovery.changed":
            discoveryHold = envelope.data?["discoveryHeld"]?["reason"]?.string
        default:
            break
        }
        return true
    }

    public var orderedSessions: [BridgeSession] {
        order.compactMap { sessions[$0] }
    }
}
