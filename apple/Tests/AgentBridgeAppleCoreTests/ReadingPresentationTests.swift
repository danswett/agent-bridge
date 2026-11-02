@testable import AgentBridgeAppleCore
import Foundation
import Testing

@Test func markdownKeepsNestedListsTablesAndRulesStructured() {
    let document = BridgeMarkdownDocument("""
    # Title

    3. Third
       - Nested first
       - Nested second
    4. Fourth

    ---

    | Name | Value |
    | :--- | ---: |
    | a \\| b | `x|y` |
    | last | |
    """)
    #expect(document.blocks.count == 4)
    guard case let .list(ordered, start, items) = document.blocks[1].kind else { Issue.record("Expected a list"); return }
    #expect(ordered && start == 3 && items.count == 2)
    guard case let .list(nestedOrdered, _, nested) = items[0].blocks[1].kind else { Issue.record("Expected a nested list"); return }
    #expect(!nestedOrdered && nested.count == 2)
    #expect(document.blocks[2].kind == .rule)
    #expect(document.blocks[3].kind == .table(headers: ["Name", "Value"], rows: [["a | b", "`x|y`"], ["last", ""]]))
    #expect(!document.isTruncated)
}

@Test func markdownFencesPreserveLiteralContentAndQuotesContainBlocks() {
    let document = BridgeMarkdownDocument("````swift\n```literal\n| not | a table |\n````\n\n> - One\n> - Two\n\nHeading\n===")
    #expect(document.blocks[0].kind == .code("```literal\n| not | a table |"))
    guard case let .quote(blocks) = document.blocks[1].kind,
          case let .list(_, _, items) = blocks[0].kind else { Issue.record("Expected a quoted list"); return }
    #expect(items.count == 2)
    #expect(document.blocks[2].kind == .heading(1, "Heading"))
}

@Test func markdownBoundsLargeDocumentsWithoutInterpretingHTMLOrImages() {
    let raw = "<script>alert('literal')</script>\n![image](https://example.invalid/pixel.png)"
    #expect(BridgeMarkdownDocument(raw).blocks.first?.kind == .paragraph(raw))
    let huge = BridgeMarkdownDocument(String(repeating: "x", count: BridgeMarkdownDocument.maximumBytes + 1))
    #expect(huge.isTruncated)
    let rows = (0 ..< 120).map { "| \($0) | value |" }.joined(separator: "\n")
    let table = BridgeMarkdownDocument("| A | B |\n| --- | --- |\n" + rows)
    guard case let .table(_, rendered) = table.blocks[0].kind else { Issue.record("Expected a table"); return }
    #expect(rendered.count == 100)
    #expect(table.isTruncated)
}

@Test func markdownInlineBackticksDoNotConsumeFollowingParagraphs() {
    let document = BridgeMarkdownDocument("```literal```\n\nNormal prose")
    #expect(document.blocks.map(\.kind) == [.paragraph("```literal```"), .paragraph("Normal prose")])
    let invalidInfo = BridgeMarkdownDocument("```swift`literal\n\nStill prose")
    #expect(invalidInfo.blocks.map(\.kind) == [.paragraph("```swift`literal"), .paragraph("Still prose")])
}

@Test func markdownBlockIdentitiesRemainStableWhenTextIsAppended() {
    let before = BridgeMarkdownDocument("First\n\n- One\n- Two")
    let after = BridgeMarkdownDocument("First\n\n- One\n- Two\n\nLast")
    #expect(before.blocks.map(\.id) == Array(after.blocks.prefix(2)).map(\.id))
    #expect(BridgeMarkdownDocument("999999999999999999999. literal").blocks.first?.kind == .paragraph("999999999999999999999. literal"))
}

@MainActor private final class HistoryGate {
    var calls: [(String, Int)] = []
    var continuations: [CheckedContinuation<JSONValue, any Error>] = []
    func request(_ id: String, version: Int) async throws -> JSONValue {
        calls.append((id, version))
        return try await withCheckedThrowingContinuation { continuations.append($0) }
    }

    func finish(id: String, version: Int) {
        continuations.removeFirst().resume(returning: .object(["id": .string(id), "version": .number(Double(version))]))
    }

    func fail() {
        continuations.removeFirst().resume(throwing: CocoaError(.fileReadUnknown))
    }
}

@MainActor private func waitForHistory(_ condition: () -> Bool) async throws {
    for _ in 0 ..< 200 {
        if condition() {
            return
        }
        try await Task.sleep(for: .milliseconds(1))
    }
    try #require(condition())
}

@MainActor @Test func historyCoalescesUpdatesWithoutCancellingTheActiveRequest() async throws {
    let gate = HistoryGate()
    let loader = BridgeHistoryLoader()
    loader.activate(sessionID: "one")
    loader.refresh { try await gate.request($0, version: 1) }
    try await waitForHistory { gate.calls.count == 1 }
    loader.refresh { try await gate.request($0, version: 2) }
    loader.refresh { try await gate.request($0, version: 3) }
    #expect(gate.calls.count == 1)
    gate.finish(id: "one", version: 1)
    try await waitForHistory { gate.calls.count == 2 }
    #expect(gate.calls.map(\.1) == [1, 3])
    gate.finish(id: "one", version: 3)
    try await waitForHistory { !loader.isLoading }
    #expect(loader.value?["version"] == .number(3))
    #expect(loader.error == nil)
}

@MainActor @Test func hiddenHistoryDiscardsLateResultsAndCanLoadAnotherSession() async throws {
    let gate = HistoryGate()
    let loader = BridgeHistoryLoader()
    loader.activate(sessionID: "old")
    loader.refresh { try await gate.request($0, version: 1) }
    try await waitForHistory { gate.calls.count == 1 }
    loader.deactivate()
    loader.activate(sessionID: "new")
    loader.refresh { try await gate.request($0, version: 2) }
    gate.finish(id: "old", version: 1)
    try await waitForHistory { gate.calls.count == 2 }
    #expect(loader.value == nil)
    #expect(gate.calls[1].0 == "new")
    gate.finish(id: "new", version: 2)
    try await waitForHistory { !loader.isLoading }
    #expect(loader.value?["id"]?.string == "new")
}

@MainActor @Test func historyReportsFailureAndAllowsAnExplicitRetry() async throws {
    let gate = HistoryGate()
    let loader = BridgeHistoryLoader()
    loader.activate(sessionID: "one")
    loader.refresh { try await gate.request($0, version: 1) }
    try await waitForHistory { gate.calls.count == 1 }
    gate.fail()
    try await waitForHistory { !loader.isLoading }
    #expect(loader.error != nil)
    loader.refresh { try await gate.request($0, version: 2) }
    try await waitForHistory { gate.calls.count == 2 }
    gate.finish(id: "wrong", version: 2)
    try await waitForHistory { !loader.isLoading }
    #expect(loader.value == nil)
    #expect(loader.error == "The host returned details for a different session.")
}
