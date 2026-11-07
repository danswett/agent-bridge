import AgentBridgeAppleCore
import CryptoKit
import Foundation

public struct BridgeUsageCacheSession: Sendable, Equatable {
    public let relay: String
    public let account: String
    fileprivate let generation: UUID
}

public actor BridgeUsageCache {
    public static let retention: TimeInterval = 14 * 24 * 60 * 60
    public static let maximumAllowances = 32
    private let directory: URL
    private var active: BridgeUsageCacheSession?
    private var entries: [BridgeAgentUsage] = []
    private var rosterRevision = -1
    private var allowedMachines = Set<String>()
    public private(set) var statusMessage: String?

    private struct Disk: Codable {
        let version: Int
        let relay: String
        let account: String
        let agents: [JSONValue]
    }

    public enum Failure: LocalizedError, Sendable {
        case inactive, invalidScope, invalidFile
        public var errorDescription: String? {
            switch self {
            case .inactive: "The usage cache belongs to a previous sign-in."
            case .invalidScope: "The usage cache requires a relay and signed-in account."
            case .invalidFile: "The saved usage report is invalid or too large."
            }
        }
    }

    public init(directory: URL? = nil) {
        self.directory = directory ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("AgentBridge/Usage", isDirectory: true)
    }

    public func activate(relay: String, account: String, now: Date = Date()) throws -> BridgeUsageCacheSession {
        let session = try scope(relay: relay, account: account)
        active = session
        rosterRevision = -1
        allowedMachines = []
        entries = []
        statusMessage = nil
        let path = file(for: session)
        if FileManager.default.fileExists(atPath: path.path) {
            do {
                let attributes = try FileManager.default.attributesOfItem(atPath: path.path)
                guard ((attributes[.size] as? NSNumber)?.intValue ?? Int.max) <= 512 * 1024 else { throw Failure.invalidFile }
                let disk = try JSONDecoder().decode(Disk.self, from: Data(contentsOf: path))
                guard disk.version == 1, disk.relay == session.relay, disk.account == session.account else { throw Failure.invalidFile }
                entries = retained(disk.agents.compactMap(BridgeAgentUsage.init), now: now)
                try persist(session)
            } catch {
                entries = []
                statusMessage = "Saved usage could not be loaded: \(BridgeShownError.sentence(for: error))"
            }
        }
        return session
    }

    public func report(session: BridgeUsageCacheSession, now: Date = Date()) throws -> JSONValue {
        try require(session)
        entries = retained(entries, now: now)
        return .object(["agents": .array(entries.map(Self.json))])
    }

    /// A peer reading is retained only while its originating machine belongs to this
    /// account's known machines. Passing a current roster also removes forgotten hosts.
    public func merge(report: BridgeUsageReport, machineID: String, machineName: String, allowedMachineIDs: Set<String>,
                      session: BridgeUsageCacheSession, now: Date = Date(), revision: Int? = nil) throws -> JSONValue
    {
        try require(session)
        let allowedMachineIDs = membership(allowedMachineIDs, revision: revision)
        entries.removeAll { !allowedMachineIDs.contains($0.readByID ?? "") }
        let additions = report.agents.compactMap { usage -> BridgeAgentUsage? in
            guard allowedMachineIDs.contains(machineID) else { return nil }
            let origin = usage.readByID ?? machineID
            guard allowedMachineIDs.contains(origin) else { return nil }
            var json = Self.fields(usage)
            if usage.readByID == nil {
                json["readBy"] = .object(["id": .string(machineID), "name": .string(String(machineName.prefix(64)))])
            }
            return BridgeAgentUsage(json: .object(json))
        }
        // Live entries come first, so exact timestamp ties prefer the current report.
        entries = retained(additions + entries, now: now)
        try persist(session)
        return try self.report(session: session, now: now)
    }

    public func forget(machineID: String, session: BridgeUsageCacheSession) throws {
        try require(session)
        entries.removeAll { $0.readByID == machineID }
        try persist(session)
    }

    public func prune(allowedMachineIDs: Set<String>, session: BridgeUsageCacheSession, revision: Int? = nil) throws {
        try require(session)
        let allowedMachineIDs = membership(allowedMachineIDs, revision: revision)
        entries.removeAll { !allowedMachineIDs.contains($0.readByID ?? "") }
        try persist(session)
    }

    private func membership(_ proposed: Set<String>, revision: Int?) -> Set<String> {
        guard let revision else { return proposed }
        if revision >= rosterRevision {
            rosterRevision = revision
            allowedMachines = proposed
        }
        return allowedMachines
    }

    public func clear(session: BridgeUsageCacheSession) throws {
        try require(session)
        try clearScope(relay: session.relay, account: session.account)
    }

    /// Sign-out names the immutable account scope, not a lease that may have rotated
    /// during reconnect. Deleting an old scope never revokes another account's lease.
    public func clearScope(relay: String, account: String) throws {
        let target = try scope(relay: relay, account: account)
        if active?.relay == target.relay, active?.account == target.account {
            // Revoke first so even failed deletion cannot let an old update restore it.
            active = nil
            entries = []
            statusMessage = nil
        }
        let path = file(for: target)
        if FileManager.default.fileExists(atPath: path.path) {
            try FileManager.default.removeItem(at: path)
        }
    }

    private func scope(relay: String, account: String) throws -> BridgeUsageCacheSession {
        guard !account.isEmpty, let url = URL(string: relay), ["http", "https"].contains(url.scheme?.lowercased() ?? ""),
              url.host != nil, url.user == nil, url.password == nil, url.query == nil, url.fragment == nil else { throw Failure.invalidScope }
        return BridgeUsageCacheSession(relay: url.absoluteString, account: account, generation: UUID())
    }

    private func require(_ session: BridgeUsageCacheSession) throws {
        guard active == session else { throw Failure.inactive }
    }

    private func retained(_ candidates: [BridgeAgentUsage], now: Date) -> [BridgeAgentUsage] {
        let recent = candidates.filter { now.timeIntervalSince($0.measuredAt) <= Self.retention && $0.measuredAt <= now.addingTimeInterval(120) }
        let grouped = Dictionary(grouping: recent, by: \.id)
        let groups = grouped.values.map { values -> [BridgeAgentUsage] in
            // Stable ties keep the first item, which is the current live report.
            let reading = values.filter { !$0.windows.isEmpty }.reduce(nil as BridgeAgentUsage?) { best, next in
                best == nil || next.measuredAt > best!.measuredAt ? next : best
            }
            let failure = values.filter(\.windows.isEmpty).reduce(nil as BridgeAgentUsage?) { best, next in
                best == nil || next.measuredAt > best!.measuredAt ? next : best
            }
            if let reading {
                return [reading] + (failure.map { $0.measuredAt > reading.measuredAt ? [$0] : [] } ?? [])
            }
            return failure.map { [$0] } ?? []
        }.sorted { ($0.map(\.measuredAt).max() ?? .distantPast) > ($1.map(\.measuredAt).max() ?? .distantPast) }
        return groups.prefix(Self.maximumAllowances).flatMap(\.self)
    }

    private func file(for session: BridgeUsageCacheSession) -> URL {
        let scope = "\(session.relay.utf8.count):\(session.relay)\(session.account.utf8.count):\(session.account)"
        let hash = SHA256.hash(data: Data(scope.utf8)).map { String(format: "%02x", $0) }.joined()
        return directory.appendingPathComponent(hash + ".json")
    }

    private func persist(_ session: BridgeUsageCacheSession) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
        let data = try JSONEncoder().encode(Disk(version: 1, relay: session.relay, account: session.account, agents: entries.map(Self.json)))
        guard data.count <= 512 * 1024 else { throw Failure.invalidFile }
        let path = file(for: session)
        try data.write(to: path, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: path.path)
    }

    private static func json(_ usage: BridgeAgentUsage) -> JSONValue {
        .object(fields(usage))
    }

    private static func fields(_ usage: BridgeAgentUsage) -> [String: JSONValue] {
        var value: [String: JSONValue] = ["agent": .string(usage.agent), "label": .string(String(usage.label.prefix(64))),
                                          "source": .string(usage.source), "measuredAt": .string(date(usage.measuredAt)),
                                          "windows": .array(usage.windows.prefix(6).map { window in
                                              var fields: [String: JSONValue] = ["key": .string(String(window.key.prefix(64))), "label": .string(String(window.label.prefix(64))), "percent": .number(window.percent)]
                                              if let used = window.used, used.isFinite {
                                                  fields["used"] = .number(used)
                                              }
                                              if let limit = window.limit, limit.isFinite {
                                                  fields["limit"] = .number(limit)
                                              }
                                              if let unit = window.unit {
                                                  fields["unit"] = .string(String(unit.prefix(32)))
                                              }
                                              if let starts = window.startsAt {
                                                  fields["startsAt"] = .string(date(starts))
                                              }
                                              if let resets = window.resetsAt {
                                                  fields["resetsAt"] = .string(date(resets))
                                              }
                                              return .object(fields)
                                          })]
        if let account = usage.account {
            value["account"] = .string(String(account.prefix(100)))
        }
        if let key = usage.accountKey {
            value["accountKey"] = .string(String(key.prefix(64)))
        }
        if let plan = usage.plan {
            value["plan"] = .string(String(plan.prefix(100)))
        }
        if let error = usage.error {
            value["error"] = .string(String(error.prefix(512)))
        }
        if let slot = usage.slot {
            value["slot"] = .number(Double(slot))
        }
        if let id = usage.readByID {
            value["readBy"] = .object(["id": .string(id), "name": .string(String((usage.readByName ?? "Unknown machine").prefix(64)))])
        }
        return value
    }

    private static func date(_ date: Date) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: date)
    }
}
