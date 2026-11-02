@testable import AgentBridgeAppleCore
import Foundation
import Testing

@Test func usageGroupsAllowancesAndKeepsReadingAgeWhenANewerReadFails() throws {
    let fixture = #"{"agents":[{"agent":"copilot","label":"GitHub Copilot","account":"Person","source":"api","measuredAt":"2026-11-02T12:00:00.000Z","readBy":{"id":"machine-1","name":"First"},"windows":[{"key":"plan","label":"Plan","percent":80}]},{"agent":"copilot","label":"GitHub Copilot","account":"person","source":"none","measuredAt":"2026-11-02T12:30:00Z","error":"Refresh failed","windows":[]},{"agent":"claude","label":"Claude Code","accountKey":"opaque","source":"api","measuredAt":"2026-11-02T12:00:00Z","windows":[{"key":"weekly","label":"Weekly","percent":20}]}]}"#
    let report = try BridgeUsageReport(json: JSONDecoder().decode(JSONValue.self, from: Data(fixture.utf8)))
    let rows = BridgeUsageReport.merged([report])
    #expect(rows.count == 2)
    #expect(rows.first?.headline == 80)
    let reading = try #require(rows.first)
    #expect(reading.error == "Refresh failed")
    #expect(reading.readByName == "First")
    #expect(reading.visibleError(at: reading.measuredAt.addingTimeInterval(3500)) == nil)
    #expect(reading.visibleError(at: reading.measuredAt.addingTimeInterval(3601)) == "Refresh failed")
}

@Test func usagePaceComesFromActualWindowDatesAndDoesNotClampTheHeadline() throws {
    let fixture = #"{"key":"plan","label":"Plan","percent":110,"startsAt":"2026-11-01T00:00:00Z","resetsAt":"2026-11-03T00:00:00Z"}"#
    let window = try #require(BridgeUsageWindow(json: JSONDecoder().decode(JSONValue.self, from: Data(fixture.utf8))))
    let start = try #require(window.startsAt)
    #expect(window.percent == 110)
    #expect(window.barPercent == 100)
    #expect(window.severity == "critical")
    #expect(window.elapsedPercent(at: start.addingTimeInterval(86400)) == 50)
    #expect(window.paceText(at: start.addingTimeInterval(86400)) == "60% ahead of pace")
    let rolling = try #require(BridgeUsageWindow(json: .object(["key": .string("primary"), "percent": .number(10)])))
    #expect(rolling.elapsedPercent(at: Date()) == nil)
    #expect(try BridgeUsageReport.operation(subscribe: true, capabilities: ["usage", "usage.shared"]).data["shared"] == .bool(true))
    #expect(throws: BridgeOperationError.self) { try BridgeUsageReport.operation(capabilities: []) }
}
