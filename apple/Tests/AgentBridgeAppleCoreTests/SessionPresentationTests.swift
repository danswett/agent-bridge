@testable import AgentBridgeAppleCore
import Foundation
import Testing

private func json(_ source: String) throws -> JSONValue {
    try JSONDecoder().decode(JSONValue.self, from: Data(source.utf8))
}

private func session(_ source: String) throws -> BridgeSession {
    try #require(BridgeSession(json: json(source)))
}

@Test func searchMatchesEveryWordAcrossFieldsAndRanksTitlePrefixesFirst() throws {
    let sessions = try [
        session(#"{"id":"older","title":"Apple client","branch":"feature/mac","updatedAt":"2026-11-01T00:00:00.000Z"}"#),
        session(#"{"id":"newer","title":"Review Apple client","branch":"feature/mac","updatedAt":"2026-11-02T00:00:00.000Z"}"#),
        session(#"{"id":"unrelated","title":"Apple orchard","branch":"main"}"#),
    ]
    #expect(SessionPresentation.search(sessions: sessions, query: "Apple client").map(\.id) == ["older", "newer"])
    #expect(SessionPresentation.search(sessions: sessions, query: "APPLE mac", machineName: "Build server").map(\.id) == ["newer", "older"])
    #expect(SessionPresentation.search(sessions: sessions, query: "server orchard", machineName: "Build server").map(\.id) == ["unrelated"])
    #expect(SessionPresentation.search(sessions: sessions, query: "  ").map(\.id) == sessions.map(\.id))
}

@Test func needsYouUsesQuestionAgeWithoutMovingTheSessionList() throws {
    let sessions = try [
        session(#"{"id":"new","question":{"askedAt":"2026-11-02T19:00:00.000Z"}}"#),
        session(#"{"id":"idle","status":"idle"}"#),
        session(#"{"id":"old","question":{"askedAt":"2026-11-02T18:00:00.000Z"}}"#),
    ]
    #expect(SessionPresentation.needsYou(sessions: sessions).map(\.id) == ["old", "new"])
    #expect(sessions.map(\.id) == ["new", "idle", "old"])
}

@Test func detailsRejectMetadataFromADifferentSession() throws {
    let current = try session(#"{"id":"current","branch":"topic","driver":"agent","activity":{"turnCount":3,"compactions":2}}"#)
    let wrong = try json(#"{"id":"previous","processId":123,"driverPrincipal":"wrong principal"}"#)
    let rows = SessionPresentation.details(session: current, fullSession: wrong, machineName: "Build server")
    #expect(!rows.contains { $0.label == "Process" })
    #expect(rows.first { $0.label == "Driven by" }?.value == "An agent")
    #expect(rows.first { $0.label == "Turns" }?.value == "3")
    #expect(rows.first { $0.label == "Branch" }?.monospaced == true)
}

@Test func detailsDoNotMixCachedStatusMetadataWithANewerLiveSession() throws {
    let current = try session(#"{"id":"one","updatedAt":"2026-11-02T19:00:00.000Z","driver":"agent"}"#)
    let previous = try json(#"{"id":"one","updatedAt":"2026-11-02T18:00:00.000Z","driverPrincipal":"old driver","statusSince":"2026-11-02T17:00:00.000Z","processId":123}"#)
    let rows = SessionPresentation.details(session: current, fullSession: previous)
    #expect(rows.first { $0.label == "Driven by" }?.value == "An agent")
    #expect(!rows.contains { $0.label == "Status since" })
    #expect(rows.first { $0.label == "Process" }?.value == "123")
}

@Test func historyShowsNewestFiftyAndKeepsLatestDuplicate() throws {
    let tools: [JSONValue] = (0 ..< 52).map { .object(["id": .string("tool-\($0)"), "name": .string("Read"), "status": .string("future")]) }
    let value = JSONValue.object(["activity": .object(["history": .array(tools)])])
    let rows = SessionPresentation.toolHistory(in: value)
    #expect(rows.count == 50)
    #expect(rows.first?.id == "tool-51")
    #expect(rows.last?.id == "tool-2")
    #expect(rows.allSatisfy { $0.status == "done" })
    let duplicate = try json(#"{"activity":{"history":[{"id":"one","name":"Old"},{"id":"one","name":"Latest"}]}}"#)
    #expect(SessionPresentation.toolHistory(in: duplicate).map(\.name) == ["Latest"])
}

@Test func usageKeepsAReadingWhenANewerSharedFailureArrives() throws {
    let report = try json(#"""
    {"agents":[
      {"agent":"copilot","label":"GitHub Copilot","account":"Example","measuredAt":"2026-11-02T18:00:00.000Z","windows":[{"key":"plan","label":"Plan","percent":79}]},
      {"agent":"copilot","label":"GitHub Copilot","account":"example","measuredAt":"2026-11-02T18:30:00.000Z","windows":[],"error":"Temporary failure"}
    ]}
    """#)
    let fresh = try SessionPresentation.usage(report: report, now: #require(SessionPresentation.date("2026-11-02T18:45:00.000Z")))
    #expect(fresh.count == 1)
    #expect(fresh.first?.headline == 79)
    #expect(fresh.first?.error == nil)
    let stale = try SessionPresentation.usage(report: report, now: #require(SessionPresentation.date("2026-11-02T20:00:00.000Z")))
    #expect(stale.first?.headline == 79)
    #expect(stale.first?.error == "Temporary failure")
    #expect(stale.first?.isStale == true)
}

@Test func usageDoesNotBorrowAnEqualTimeFailure() throws {
    let report = try json(#"""
    {"agents":[
      {"agent":"codex","measuredAt":"2026-11-02T18:00:00.000Z","windows":[{"key":"weekly","percent":40}],"error":"Reading warning"},
      {"agent":"codex","measuredAt":"2026-11-02T18:00:00.000Z","windows":[],"error":"Peer failure"}
    ]}
    """#)
    let rows = try SessionPresentation.usage(report: report, now: #require(SessionPresentation.date("2026-11-02T20:00:00.000Z")))
    #expect(rows.first?.error == "Reading warning")
}

@Test func usageSeparatesAccountsAndExpiresOldReadingsBeforeSelecting() throws {
    let report = try json(#"""
    {"agents":[
      {"agent":"claude","accountKey":"first","label":"Claude","measuredAt":"2026-10-01T00:00:00.000Z","windows":[{"key":"weekly","percent":100}]},
      {"agent":"claude","accountKey":"first","label":"Claude","measuredAt":"2026-11-02T00:00:00.000Z","windows":[],"error":"No current reading"},
      {"agent":"claude","accountKey":"second","label":"Claude","measuredAt":"2026-11-02T00:00:00.000Z","windows":[{"key":"weekly","percent":0}]}
    ]}
    """#)
    let rows = try SessionPresentation.usage(report: report, now: #require(SessionPresentation.date("2026-11-02T00:10:00.000Z")))
    #expect(rows.count == 2)
    #expect(rows.first?.headline == 0)
    #expect(rows.first?.account == "#seco")
    #expect(rows.last?.headline == nil)
    #expect(rows.last?.account == "#firs")
    #expect(rows.last?.error == "No current reading")
}

@Test func usagePaceRequiresAnActualWindowStartAndKeepsOverage() throws {
    let report = try json(#"""
    {"agents":[{"agent":"codex","label":"Codex","measuredAt":"2026-11-02T12:00:00.000Z","windows":[
      {"key":"rolling","percent":20,"resetsAt":"2026-11-03T00:00:00.000Z"},
      {"key":"fixed","percent":125,"startsAt":"2026-11-02T00:00:00.000Z","resetsAt":"2026-11-03T00:00:00.000Z"}
    ]}]}
    """#)
    let rows = try SessionPresentation.usage(report: report, now: #require(SessionPresentation.date("2026-11-02T12:00:00.000Z")))
    let windows = try #require(rows.first?.windows)
    #expect(windows[0].elapsedPercent == nil)
    #expect(windows[0].paceText == nil)
    #expect(windows[1].percent == 125)
    #expect(windows[1].progress == 100)
    #expect(windows[1].elapsedPercent == 50)
    #expect(windows[1].aheadOfPace)
}

@Test func readingTimesAndResetsRemainReadableAtBoundaries() throws {
    let now = try #require(SessionPresentation.date("2026-11-02T12:00:00.000Z"))
    #expect(SessionPresentation.age(nil, now: now) == "Reading time unavailable")
    #expect(SessionPresentation.age(now.addingTimeInterval(60), now: now) == "Just now")
    #expect(SessionPresentation.age(now.addingTimeInterval(-3600), now: now) == "1h ago")
    #expect(SessionPresentation.reset(now, now: now) == "Reset due")
    #expect(SessionPresentation.reset(now.addingTimeInterval(20), now: now) == "Resets in 1m")
}
