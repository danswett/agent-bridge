import Foundation

/// Shared presentation rules; none of these helpers reads a host or changes a session.
public enum SessionPresentation {
    public struct Detail: Identifiable, Sendable, Equatable {
        public var id: String {
            label
        }

        public let label: String
        public let value: String
        public let monospaced: Bool
    }

    public struct Tool: Identifiable, Sendable, Equatable {
        public let id: String
        public let name: String
        public let summary: String?
        public let status: String
        public let startedAt: Date?
        public let endedAt: Date?
    }

    public enum Severity: Sendable { case normal, warning, critical }

    public struct UsageWindow: Identifiable, Sendable, Equatable {
        public let id: String
        public let label: String
        public let percent: Double
        public let used: Double?
        public let limit: Double?
        public let unit: String?
        public let resetsAt: Date?
        public let elapsedPercent: Double?

        public var progress: Double {
            min(100, max(0, percent))
        }

        public var severity: Severity {
            percent >= 90 ? .critical : percent >= 75 ? .warning : .normal
        }

        public var aheadOfPace: Bool {
            elapsedPercent.map { percent - $0 > 10 } ?? false
        }

        public var paceText: String? {
            guard let elapsedPercent else { return nil }
            let difference = percent - elapsedPercent
            if difference > 10 {
                return "\(difference.formatted(.number.precision(.fractionLength(0))))% ahead of pace"
            }
            if difference < -10 {
                return "\((-difference).formatted(.number.precision(.fractionLength(0))))% under pace"
            }
            return "On pace"
        }
    }

    public struct UsageAllowance: Identifiable, Sendable, Equatable {
        public let id: String
        public let label: String
        public let account: String?
        public let plan: String?
        public let source: String?
        public let measuredAt: Date?
        public let readBy: String?
        public let windows: [UsageWindow]
        public let error: String?
        public let isStale: Bool
        public var headline: Double? {
            windows.map(\.percent).max()
        }
    }

    public static func search(sessions: [BridgeSession], query: String, machineName: String? = nil) -> [BridgeSession] {
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines)
        let words = query.split(whereSeparator: \.isWhitespace).map(String.init)
        guard !words.isEmpty else { return sessions }
        return sessions.filter { session in
            let text = [session.title, session.agent, session.repository, session.branch, session.cwd, machineName]
                .compactMap(\.self).joined(separator: " ")
            return words.allSatisfy { text.localizedCaseInsensitiveContains($0) }
        }.sorted { lhs, rhs in
            let leftPrefix = lhs.title.lowercased().hasPrefix(query.lowercased())
            let rightPrefix = rhs.title.lowercased().hasPrefix(query.lowercased())
            if leftPrefix != rightPrefix {
                return leftPrefix
            }
            if lhs.updatedAt != rhs.updatedAt {
                return (lhs.updatedAt ?? "") > (rhs.updatedAt ?? "")
            }
            return lhs.id < rhs.id
        }
    }

    public static func needsYou(sessions: [BridgeSession]) -> [BridgeSession] {
        sessions.filter { $0.question != nil }.sorted {
            let left = $0.question?["askedAt"]?.string ?? ""
            let right = $1.question?["askedAt"]?.string ?? ""
            return left == right ? $0.id < $1.id : left < right
        }
    }

    public static func details(session: BridgeSession, fullSession: JSONValue? = nil, machineName: String? = nil) -> [Detail] {
        // A late detail response must never supply metadata for a different session.
        let full = fullSession?["id"]?.string == session.id ? fullSession : nil
        let currentFull = session.updatedAt != nil && full?["updatedAt"]?.string == session.updatedAt ? full : nil
        var result: [Detail] = []
        func add(_ label: String, _ value: String?, mono: Bool = false) {
            guard let value, !value.isEmpty else { return }
            result.append(Detail(label: label, value: value, monospaced: mono))
        }
        add("Status", session.statusText)
        add("Agent", session.agent.capitalized)
        add("Machine", machineName)
        add("Model", session.model)
        add("Repository", session.repository)
        add("Branch", session.branch, mono: true)
        add("Working directory", session.cwd, mono: true)
        if session.driver == "agent" {
            add("Driven by", currentFull?["driverPrincipal"]?.string ?? "An agent")
        }
        if let turns = wholeNumber(session.activity?["turnCount"]) {
            add("Turns", String(turns))
        }
        if let compactions = wholeNumber(session.activity?["compactions"]), compactions > 0 {
            add("Compactions", String(compactions))
        }
        if let pid = wholeNumber(full?["processId"]) {
            add("Process", String(pid), mono: true)
        }
        if let started = date(full?["startedAt"]?.string) {
            add("Started", timestamp(started))
        }
        if let changed = date(currentFull?["statusSince"]?.string) {
            add("Status since", timestamp(changed))
        }
        if let updated = date(session.updatedAt) {
            add("Updated", timestamp(updated))
        }
        add("Session ID", session.id, mono: true)
        return result
    }

    /// session.get sends oldest first; the tools pane shows at most 50, newest first.
    public static func toolHistory(in fullSession: JSONValue) -> [Tool] {
        var seen = Set<String>()
        return (fullSession["activity"]?["history"]?.array ?? []).suffix(50).reversed().compactMap { value in
            guard let id = value["id"]?.string, !id.isEmpty, seen.insert(id).inserted else { return nil }
            let status = value["status"]?.string ?? "done"
            return Tool(id: id, name: value["name"]?.string ?? "Tool", summary: value["summary"]?.string,
                        status: ["running", "done", "failed"].contains(status) ? status : "done",
                        startedAt: date(value["startedAt"]?.string), endedAt: date(value["endedAt"]?.string))
        }
    }

    /// Shared readings of the same allowance collapse to its newest successful reading.
    /// A newer failure lends its error without erasing the last measured windows.
    public static func usage(report: JSONValue, now: Date = .now) -> [UsageAllowance] {
        var groups: [String: [JSONValue]] = [:]
        for value in report["agents"]?.array ?? [] {
            guard case .object = value else { continue }
            let agent = value["agent"]?.string ?? "unknown"
            let account = value["accountKey"]?.string ?? value["account"]?.string?.lowercased() ?? ""
            groups[agent + "\n" + account, default: []].append(value)
        }
        return groups.compactMap { key, values in
            let usable = values.filter { value in
                guard let measured = date(value["measuredAt"]?.string) else { return true }
                return now.timeIntervalSince(measured) <= 14 * 24 * 60 * 60
            }
            guard !usable.isEmpty else { return nil }
            let ordered = usable.sorted { (date($0["measuredAt"]?.string) ?? .distantPast) > (date($1["measuredAt"]?.string) ?? .distantPast) }
            let reading = ordered.first { !windows($0, now: now).isEmpty } ?? ordered[0]
            let measuredAt = date(reading["measuredAt"]?.string)
            let parsedWindows = windows(reading, now: now)
            let stale = measuredAt.map { now.timeIntervalSince($0) > 3600 } ?? true
            let failure = ordered.first { value in
                windows(value, now: now).isEmpty && value["error"]?.string?.isEmpty == false
                    && (date(value["measuredAt"]?.string) ?? .distantPast) > (measuredAt ?? .distantPast)
            }
            let account = reading["account"]?.string ?? reading["accountKey"]?.string.map { "#" + $0.prefix(4) }
            return UsageAllowance(id: key, label: reading["label"]?.string ?? reading["agent"]?.string?.capitalized ?? "Agent",
                                  account: account, plan: reading["plan"]?.string, source: reading["source"]?.string,
                                  measuredAt: measuredAt, readBy: reading["readBy"]?["name"]?.string, windows: parsedWindows,
                                  error: parsedWindows.isEmpty || stale ? failure?["error"]?.string ?? reading["error"]?.string : nil,
                                  isStale: stale)
        }.sorted {
            if $0.headline != $1.headline {
                return ($0.headline ?? -1) > ($1.headline ?? -1)
            }
            return $0.id < $1.id
        }
    }

    private static func windows(_ value: JSONValue, now: Date) -> [UsageWindow] {
        var seen = Set<String>()
        return (value["windows"]?.array ?? []).prefix(6).compactMap { window in
            guard let id = window["key"]?.string, !id.isEmpty, seen.insert(id).inserted,
                  let percent = finiteNumber(window["percent"]), percent >= 0 else { return nil }
            let resets = date(window["resetsAt"]?.string)
            var elapsed: Double?
            if let start = date(window["startsAt"]?.string), let resets, resets > start {
                elapsed = min(100, max(0, now.timeIntervalSince(start) / resets.timeIntervalSince(start) * 100))
            }
            return UsageWindow(id: id, label: window["label"]?.string ?? id, percent: percent,
                               used: finiteNumber(window["used"]), limit: finiteNumber(window["limit"]), unit: window["unit"]?.string,
                               resetsAt: resets, elapsedPercent: elapsed)
        }
    }

    public static func date(_ value: String?) -> Date? {
        guard let value else { return nil }
        return (try? Date.ISO8601FormatStyle(includingFractionalSeconds: true).parse(value))
            ?? (try? Date.ISO8601FormatStyle().parse(value))
    }

    public static func timestamp(_ date: Date) -> String {
        var style = Date.FormatStyle(date: .abbreviated, time: .shortened)
        style.timeZone = TimeZone(identifier: "America/Los_Angeles") ?? .current
        return date.formatted(style) + " PT"
    }

    public static func age(_ date: Date?, now: Date = .now) -> String {
        guard let date else { return "Reading time unavailable" }
        let seconds = max(0, now.timeIntervalSince(date))
        if seconds < 90 {
            return "Just now"
        }
        if seconds < 3600 {
            return "\(Int(seconds / 60))m ago"
        }
        if seconds < 86400 {
            return "\(Int(seconds / 3600))h ago"
        }
        return "\(Int(seconds / 86400))d ago"
    }

    public static func reset(_ date: Date, now: Date = .now) -> String {
        let seconds = date.timeIntervalSince(now)
        guard seconds > 0 else { return "Reset due" }
        if seconds >= 86400 {
            return "Resets in \(Int((seconds / 86400).rounded()))d"
        }
        if seconds >= 3600 {
            return "Resets in \(Int((seconds / 3600).rounded()))h"
        }
        return "Resets in \(max(1, Int((seconds / 60).rounded())))m"
    }

    private static func finiteNumber(_ value: JSONValue?) -> Double? {
        guard let number = value?.number, number.isFinite else { return nil }
        return number
    }

    private static func wholeNumber(_ value: JSONValue?) -> Int? {
        guard let number = finiteNumber(value), number >= 0, number < Double(Int.max), number.rounded(.towardZero) == number else { return nil }
        return Int(number)
    }
}
