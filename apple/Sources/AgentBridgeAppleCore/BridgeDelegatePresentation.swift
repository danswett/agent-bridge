import Foundation

public enum BridgeDelegatePresentation {
    public struct Entry: Identifiable, Sendable, Equatable {
        public var id: String {
            delegate.id
        }

        public let delegate: BridgeDelegate
        public let depth: Int
        public let parentName: String?
    }

    public struct Groups: Sendable, Equatable {
        public let active: [Entry]
        public let finished: [Entry]
    }

    public struct Event: Identifiable, Sendable, Equatable {
        public let id: String
        public let delegateID: String?
        public let text: String
        public let at: Date
    }

    public static func groups(_ delegates: [BridgeDelegate]) -> Groups {
        let list = unique(delegates)
        let names = Dictionary(uniqueKeysWithValues: list.map { ($0.id, $0.name) })
        return Groups(active: nest(list.filter(\.isRunning) + list.filter { !$0.isRunning && $0.status != "ended" }, names: names),
                      finished: nest(list.filter { $0.status == "ended" }, names: names))
    }

    public static func descendants(of id: String, in delegates: [BridgeDelegate]) -> [Entry] {
        let list = unique(delegates)
        let children = Dictionary(grouping: list.filter { $0.parent != nil && $0.parent != $0.id }, by: { $0.parent! })
        let names = Dictionary(uniqueKeysWithValues: list.map { ($0.id, $0.name) })
        var seen: Set<String> = [id]
        var stack = (children[id] ?? []).reversed().map { ($0, 0) }
        var result: [Entry] = []
        while let (delegate, depth) = stack.popLast() {
            guard seen.insert(delegate.id).inserted else { continue }
            result.append(Entry(delegate: delegate, depth: depth, parentName: delegate.parent.flatMap { names[$0] }))
            stack += (children[delegate.id] ?? []).reversed().map { ($0, depth + 1) }
        }
        return result
    }

    private static func nest(_ list: [BridgeDelegate], names: [String: String]) -> [Entry] {
        let ids = Set(list.map(\.id))
        let children = Dictionary(grouping: list.filter { $0.parent != nil && $0.parent != $0.id && ids.contains($0.parent!) }, by: { $0.parent! })
        let roots = list.filter { $0.parent == nil || $0.parent == $0.id || !ids.contains($0.parent!) }
        var seen = Set<String>()
        var result: [Entry] = []
        for root in roots + list {
            var stack = [(root, 0)]
            while let (delegate, depth) = stack.popLast() {
                guard seen.insert(delegate.id).inserted else { continue }
                result.append(Entry(delegate: delegate, depth: depth, parentName: delegate.parent.flatMap { names[$0] }))
                stack += (children[delegate.id] ?? []).reversed().map { ($0, depth + 1) }
            }
        }
        return result
    }

    private static func unique(_ delegates: [BridgeDelegate]) -> [BridgeDelegate] {
        var seen = Set<String>()
        return delegates.filter { seen.insert($0.id).inserted }
    }

    public static func events(_ delegates: [BridgeDelegate]) -> [Event] {
        let mine = unique(delegates).filter { $0.parent == nil }
        let starts = mine.filter { $0.startedAt != nil }.sorted {
            $0.startedAt == $1.startedAt ? $0.id < $1.id : $0.startedAt! < $1.startedAt!
        }
        var result: [Event] = []
        var index = 0
        while index < starts.count {
            let first = starts[index]
            var batch = [first]
            index += 1
            while index < starts.count, starts[index].startedAt!.timeIntervalSince(batch.last!.startedAt!) <= 60 {
                batch.append(starts[index]); index += 1
            }
            let text = batch.count == 1 ? "Started \(BridgeDelegateLabel.titled(first.name))" : "Started \(batch.count) delegates"
            result.append(Event(id: "start:\(first.id)", delegateID: batch.count == 1 ? first.id : nil, text: text, at: first.startedAt!))
        }
        for delegate in mine where delegate.endedAt != nil && !delegate.isRunning {
            let words = delegate.outcome == "failed" ? "failed" : delegate.outcome == "done" || delegate.status == "idle" ? "reported back" : "ended"
            result.append(Event(id: "end:\(delegate.id)", delegateID: delegate.id, text: "\(BridgeDelegateLabel.titled(delegate.name)) \(words)", at: delegate.endedAt!))
        }
        return result.sorted { $0.at == $1.at ? $0.id < $1.id : $0.at < $1.at }
    }

    public static func validateDetail(_ value: JSONValue, expectedID: String) throws -> JSONValue {
        if value == .null {
            return .object(["missing": .bool(true)])
        }
        if value["missing"]?.bool == true {
            return value
        }
        // Old hosts can ignore `agent` and return the root Session. Require the delegate shape too.
        guard value["id"]?.string == expectedID, value["kind"]?.string != nil, value["status"]?.string != nil else {
            throw BridgeOperationError("The host returned something other than the selected delegate.")
        }
        return value
    }

    public static func tools(_ delegate: BridgeDelegate, listed: BridgeDelegate? = nil) -> [SessionPresentation.Tool] {
        let history = delegate.history.isEmpty ? (listed ?? delegate).currentTool.map { [$0] } ?? [] : delegate.history
        return SessionPresentation.toolHistory(in: .object(["activity": .object(["history": .array(history)])]))
    }

    public static func duration(from start: Date?, to end: Date) -> String? {
        guard let start else { return nil }
        let value = end.timeIntervalSince(start)
        guard value.isFinite else { return nil }
        let seconds = Int(max(0, min(1_000_000_000, value)))
        if seconds >= 3600 {
            return "\(seconds / 3600)h \(seconds % 3600 / 60)m"
        }
        if seconds >= 60 {
            return "\(seconds / 60)m \(seconds % 60)s"
        }
        return "\(seconds)s"
    }

    public static func stopDraft(delegateID: String, existing: String) -> String? {
        guard let id = BridgeDelegateLabel.safeID(delegateID) else { return nil }
        let request = "Please stop your delegate \(id)."
        if existing.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return request
        }
        return existing + (existing.last?.isWhitespace == true ? "" : " ") + request
    }
}

/// List fields may be absent because of the host's frame budget. A delayed fetch
/// cannot replace a task/line that a newer list update supplied while it was in flight.
public struct BridgeDelegateContent: Sendable, Equatable {
    public struct Revision: Sendable, Equatable {
        fileprivate let line: Int
        fileprivate let task: Int
    }

    public private(set) var listed: BridgeDelegate?
    public private(set) var fetched: BridgeDelegate?
    public private(set) var task = ""
    public private(set) var line = ""
    private var listLine: String?
    private var listTask: String?
    private var lineRevision = 0
    private var taskRevision = 0
    private var responseRevision: Int?
    public init() {}
    public var revision: Revision {
        Revision(line: lineRevision, task: taskRevision)
    }

    public var latest: String {
        responseRevision == lineRevision ? fetched?.response.flatMap { $0.isEmpty ? nil : $0 } ?? line : line
    }

    public var latestTruncated: Bool {
        responseRevision == lineRevision && fetched?.response?.isEmpty == false && fetched?.responseTruncated == true
    }

    public mutating func update(_ entry: BridgeDelegate) {
        if listed?.id != entry.id {
            self = Self()
        }
        listed = entry
        if let value = entry.line, value != listLine {
            line = value; lineRevision += 1
        }
        if let value = entry.task, value != listTask {
            task = value; taskRevision += 1
        }
        listLine = entry.line; listTask = entry.task
    }

    public mutating func clearFetched() {
        fetched = nil
        responseRevision = nil
        if let listLine {
            line = listLine
        }
        if let listTask {
            task = listTask
        }
    }

    public mutating func accept(_ detail: BridgeDelegate, revision: Revision) {
        guard detail.id == listed?.id else { return }
        fetched = detail
        responseRevision = revision.line == lineRevision ? lineRevision : nil
        if let value = detail.line, revision.line == lineRevision {
            line = value
        }
        if let value = detail.task, revision.task == taskRevision {
            task = value
        }
    }
}
