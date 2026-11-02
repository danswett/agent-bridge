import Foundation

public struct BridgeUsageWindow: Identifiable, Sendable, Equatable {
    public var id: String {
        key
    }

    public let key: String
    public let label: String
    public let percent: Double
    public let used: Double?
    public let limit: Double?
    public let unit: String?
    public let startsAt: Date?
    public let resetsAt: Date?
    public var barPercent: Double {
        min(100, max(0, percent))
    }

    public var severity: String {
        percent >= 90 ? "critical" : percent >= 75 ? "warning" : "normal"
    }

    public init?(json: JSONValue) {
        guard let key = json["key"]?.string, let percent = json["percent"]?.number, percent.isFinite, percent >= 0 else { return nil }
        self.key = key
        self.percent = percent
        label = json["label"]?.string ?? key
        used = json["used"]?.number
        limit = json["limit"]?.number
        unit = json["unit"]?.string
        startsAt = BridgeUsageDate.parse(json["startsAt"]?.string)
        resetsAt = BridgeUsageDate.parse(json["resetsAt"]?.string)
    }

    public func elapsedPercent(at now: Date) -> Double? {
        guard let startsAt, let resetsAt, resetsAt > startsAt else { return nil }
        return (min(1, max(0, now.timeIntervalSince(startsAt) / resetsAt.timeIntervalSince(startsAt))) * 1000).rounded() / 10
    }

    public func paceText(at now: Date) -> String? {
        guard let elapsed = elapsedPercent(at: now) else { return nil }
        let ahead = percent - elapsed
        if ahead > 10 {
            return "\(String(format: "%.0f", ahead))% ahead of pace"
        }
        if ahead < -10 {
            return "\(String(format: "%.0f", -ahead))% under pace"
        }
        return "on pace"
    }
}

public struct BridgeAgentUsage: Identifiable, Sendable, Equatable {
    /// The account key, when supplied, groups an allowance without exposing its account.
    public var id: String {
        agent + "|" + (accountKey ?? account?.lowercased() ?? "")
    }

    public let agent: String
    public let label: String
    public let account: String?
    public let accountKey: String?
    public let plan: String?
    public let source: String
    public let windows: [BridgeUsageWindow]
    public let measuredAt: Date
    public private(set) var error: String?
    public let readByID: String?
    public let readByName: String?
    public let slot: Int?
    public var headline: Double? {
        windows.map(\.percent).max()
    }

    public init?(json: JSONValue) {
        guard let measuredAt = BridgeUsageDate.parse(json["measuredAt"]?.string) else { return nil }
        let rawAgent = json["agent"]?.string ?? "unknown"
        agent = ["copilot", "claude", "codex"].contains(rawAgent) ? rawAgent : "unknown"
        label = json["label"]?.string ?? rawAgent
        account = json["account"]?.string
        accountKey = json["accountKey"]?.string
        plan = json["plan"]?.string
        let rawSource = json["source"]?.string ?? "none"
        source = ["api", "cache", "transcript", "none"].contains(rawSource) ? rawSource : "none"
        windows = (json["windows"]?.array ?? []).compactMap(BridgeUsageWindow.init)
        self.measuredAt = measuredAt
        error = json["error"]?.string
        readByID = json["readBy"]?["id"]?.string
        readByName = json["readBy"]?["name"]?.string
        if let slot = json["slot"]?.number, (2 ... 4).contains(slot), slot.rounded() == slot {
            self.slot = Int(slot)
        } else {
            slot = nil
        }
    }

    public func isStale(at now: Date) -> Bool {
        now.timeIntervalSince(measuredAt) > 3600
    }

    public func visibleError(at now: Date) -> String? {
        windows.isEmpty || isStale(at: now) ? error : nil
    }

    fileprivate func withNewerFailure(_ failure: BridgeAgentUsage?) -> Self {
        var reading = self
        if let failure, failure.measuredAt > measuredAt, let error = failure.error {
            reading.error = error
        }
        return reading
    }
}

public struct BridgeUsageReport: Sendable, Equatable {
    public let agents: [BridgeAgentUsage]

    public init(json: JSONValue) {
        agents = (json["agents"]?.array ?? []).compactMap(BridgeAgentUsage.init)
    }

    public static func operation(subscribe: Bool = false, capabilities: Set<String>) throws -> BridgeOperation {
        try BridgeOperationValidation.require("usage", in: capabilities)
        return BridgeOperation(op: subscribe ? "usage.subscribe" : "usage.get", data: capabilities.contains("usage.shared") ? ["shared": .bool(true)] : [:])
    }

    /// A real reading outranks a newer failure. Only the newer failure's explanation is
    /// carried alongside it; it must not replace the reading's age or source machine.
    public static func merged(_ reports: [BridgeUsageReport]) -> [BridgeAgentUsage] {
        let groups = Dictionary(grouping: reports.flatMap(\.agents), by: \.id)
        return groups.values.compactMap { group -> BridgeAgentUsage? in
            let readings = group.filter { !$0.windows.isEmpty }
            let failures = group.filter(\.windows.isEmpty)
            let newestFailure = failures.max { $0.measuredAt < $1.measuredAt }
            return readings.max { $0.measuredAt < $1.measuredAt }?.withNewerFailure(newestFailure) ?? newestFailure
        }.sorted { left, right in
            if (left.headline ?? 0) == (right.headline ?? 0) {
                return left.id < right.id
            }
            return (left.headline ?? 0) > (right.headline ?? 0)
        }
    }
}

private enum BridgeUsageDate {
    static func parse(_ string: String?) -> Date? {
        guard let string else { return nil }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: string) {
            return date
        }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: string)
    }
}
