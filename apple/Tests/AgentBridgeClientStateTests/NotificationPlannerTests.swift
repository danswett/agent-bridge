import AgentBridgeAppleCore
@testable import AgentBridgeClientState
import Foundation
import Testing

private func noticeSession(id: String = "s", status: String = "waiting", question: String? = "q", message: String = "Secret prompt", background: Int = 0, version: String = "v1") throws -> BridgeSession {
    var json: [String: JSONValue] = ["id": .string(id), "title": .string("/private/workspace"), "agent": .string("codex"), "status": .string(status), "updatedAt": .string(version), "activity": .object(["response": .string("A completed turn."), "background": .object(["agents": .number(Double(background))])])]
    if let question {
        json["question"] = .object(["id": .string(question), "message": .string(message)])
    }
    return try #require(BridgeSession(json: .object(json)))
}

@Test func notificationPreviewsHideSessionAndPromptTextByDefault() throws {
    let session = try noticeSession()
    let plan = BridgeNotificationPlanner.plan(machineID: "machine", machineName: "Private Mac", before: [], after: [session])
    let notice = try #require(plan.notices.first)
    #expect(notice.title == "Agent Bridge")
    #expect(!notice.body.contains("Secret"))
    #expect(!notice.body.contains("Private Mac"))
    #expect(!notice.id.contains("machine"))
    #expect(plan.waitingCount == 1)
}

@Test func unchangedQuestionsDoNotRepeatAndResolvedQuestionsAreRemoved() throws {
    let old = try noticeSession()
    let initial = BridgeNotificationPlanner.plan(machineID: "m", machineName: "Mac", before: [], after: [old])
    let updated = try noticeSession(message: "A longer explanation", version: "v2")
    #expect(BridgeNotificationPlanner.plan(machineID: "m", machineName: "Mac", before: [old], after: [updated]).notices.isEmpty)
    let resolved = try noticeSession(status: "working", question: nil)
    let cleared = BridgeNotificationPlanner.plan(machineID: "m", machineName: "Mac", before: [updated], after: [resolved])
    #expect(cleared.removeIDs == initial.notices.map(\.id))
    #expect(cleared.waitingCount == 0)
    let ended = BridgeNotificationPlanner.plan(machineID: "m", machineName: "Mac", before: [old], after: [])
    #expect(ended.removeIDs == initial.notices.map(\.id))
}

@Test func replacementQuestionsRemoveTheOldNoticeAndBindTheNewQuestion() throws {
    let old = try noticeSession()
    let next = try noticeSession(question: "new")
    let first = BridgeNotificationPlanner.plan(machineID: "m", machineName: "Mac", before: [], after: [old])
    let replaced = BridgeNotificationPlanner.plan(machineID: "m", machineName: "Mac", before: [old], after: [next])
    #expect(replaced.removeIDs == first.notices.map(\.id))
    let newNotice = try #require(replaced.notices.first)
    #expect(newNotice.questionID == "new")
    #expect(!BridgeNotificationPlanner.isCurrent(newNotice, session: old))
    #expect(BridgeNotificationPlanner.isCurrent(newNotice, session: next))
}

@Test func initialSnapshotsNotifyPendingQuestionsButDoNotAnnounceOldFinishedTurns() throws {
    let plan = try BridgeNotificationPlanner.plan(machineID: "m", machineName: "Mac", before: [], after: [noticeSession()], initialSnapshot: true)
    #expect(plan.notices.count == 1)
    #expect(plan.notices.first?.kind == .question)
    #expect(plan.waitingCount == 1)
    let oldFinish = try noticeSession(status: "idle", question: nil)
    #expect(BridgeNotificationPlanner.plan(machineID: "m", machineName: "Mac", before: [], after: [oldFinish], initialSnapshot: true).notices.isEmpty)
}

@Test func delegatedAndEndingSessionsDoNotPretendTheTurnHasFinished() throws {
    let working = try noticeSession(status: "working", question: nil)
    let delegated = try noticeSession(status: "idle", question: nil, background: 2)
    #expect(BridgeNotificationPlanner.plan(machineID: "m", machineName: "Mac", before: [working], after: [delegated]).notices.isEmpty)
    let ending = try noticeSession(status: "ending")
    let endingPlan = BridgeNotificationPlanner.plan(machineID: "m", machineName: "Mac", before: [], after: [ending])
    #expect(endingPlan.notices.isEmpty)
    #expect(endingPlan.waitingCount == 0)
    let done = try noticeSession(status: "idle", question: nil)
    #expect(BridgeNotificationPlanner.plan(machineID: "m", machineName: "Mac", before: [working], after: [done]).notices.first?.kind == .finished)
}

@Test func optedInNotificationPreviewsAreShortAndContainNoControlCharacters() throws {
    let session = try noticeSession(message: String(repeating: "🦋\n\t", count: 300))
    let plan = BridgeNotificationPlanner.plan(machineID: "m", machineName: "Mac", before: [], after: [session], showPreviews: true)
    let notice = try #require(plan.notices.first)
    #expect(notice.body.count <= 200)
    #expect(!notice.body.contains("\n"))
    #expect(notice.title == "/private/workspace")
}
