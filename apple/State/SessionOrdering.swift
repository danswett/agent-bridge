import Foundation

@MainActor
enum SessionOrdering {
    static func sorted(_ entries: [SessionEntry], pinned: Set<String>, manualOrder: [String], sortOrder: String) -> [SessionEntry] {
        // The comparator runs thousands of times for a large fleet. Index the
        // saved order once, preserving the first occurrence of duplicate IDs.
        var ranks: [String: Int] = [:]
        ranks.reserveCapacity(manualOrder.count)
        for (index, id) in manualOrder.enumerated() where ranks[id] == nil {
            ranks[id] = index
        }
        return entries.sorted { lhs, rhs in
            if pinned.contains(lhs.id) != pinned.contains(rhs.id) {
                return pinned.contains(lhs.id)
            }
            if sortOrder == "recent", lhs.session.updatedAt != rhs.session.updatedAt {
                return (lhs.session.updatedAt ?? "") > (rhs.session.updatedAt ?? "")
            }
            if sortOrder == "machine", lhs.machineName != rhs.machineName {
                return lhs.machineName.localizedStandardCompare(rhs.machineName) == .orderedAscending
            }
            let left = ranks[lhs.id] ?? Int.max
            let right = ranks[rhs.id] ?? Int.max
            return left == right ? lhs.id < rhs.id : left < right
        }
    }
}
