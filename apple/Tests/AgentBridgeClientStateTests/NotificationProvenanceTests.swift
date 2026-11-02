import AgentBridgeAppleCore
@testable import AgentBridgeClientState
import Testing

private func provenanceSession(_ status: String, response: String? = "Earlier main response", delegate: String? = nil,
                               delegateResponse: String? = nil, id: String = "session") throws -> BridgeSession
{
    var activity: [String: JSONValue] = [:]
    if let response {
        activity["response"] = .string(response)
    }
    if let delegateResponse {
        activity["response"] = .string(delegateResponse)
        activity["source"] = .object(["response": .string("delegate")])
        activity["own"] = .object(response.map { ["response": .string($0)] } ?? [:])
    }
    var json: [String: JSONValue] = ["id": .string(id), "status": .string(status), "activity": .object(activity)]
    if status == "waiting" {
        var question: [String: JSONValue] = ["id": .string("question"), "message": .string("Continue?")]
        if let delegate {
            question["agent"] = .string(delegate)
        }
        json["question"] = .object(question)
    }
    return try #require(BridgeSession(json: .object(json)))
}

private struct NotificationSequence {
    var history = BridgeNotificationPlanner.History()

    mutating func update(_ sessions: [BridgeSession], machine: String = "machine", initial: Bool = false) -> BridgeNotificationPlan {
        history.plan(machineID: machine, machineName: "Mac", after: sessions, showPreviews: true, initialSnapshot: initial)
    }

    mutating func clear(machine: String, session: String? = nil) {
        history.clear(machineID: machine, sessionID: session)
    }
}

@Test func answeringOnlyADelegateDoesNotAnnounceAnUnchangedMainResponse() throws {
    var sequence = NotificationSequence()
    _ = try sequence.update([provenanceSession("idle")], initial: true)
    let question = try sequence.update([provenanceSession("waiting", delegate: "delegate", delegateResponse: "Delegate asks")])
    #expect(question.notices.map(\.kind) == [.question])
    _ = try sequence.update([provenanceSession("waiting", delegate: "delegate", delegateResponse: "Different delegate words")])
    let done = try sequence.update([provenanceSession("idle", delegateResponse: "Delegate finished")])
    #expect(done.notices.isEmpty)
    #expect(done.removeIDs == question.notices.map(\.id))
}

@Test func aDelegateWaitDuringTheMainTurnStillEndsThatTurnAfterRepeatedUpserts() throws {
    var sequence = NotificationSequence()
    _ = try sequence.update([provenanceSession("working")], initial: true)
    _ = try sequence.update([provenanceSession("waiting", delegate: "first")])
    _ = try sequence.update([provenanceSession("waiting", delegate: "second")])
    _ = try sequence.update([provenanceSession("waiting", delegate: "second")], initial: true)
    let done = try sequence.update([provenanceSession("idle")])
    #expect(done.notices.map(\.kind) == [.finished])
}

@Test func aMainQuestionFollowedByADelegateQuestionKeepsItsTurnProvenance() throws {
    var sequence = NotificationSequence()
    _ = try sequence.update([provenanceSession("waiting")], initial: true)
    _ = try sequence.update([provenanceSession("waiting", delegate: "delegate")])
    #expect(try sequence.update([provenanceSession("idle")]).notices.map(\.kind) == [.finished])
}

@Test func aWaitFoundAtStartupOnlyFinishesWhenTheMainResponseChanges() throws {
    for changed in [false, true] {
        var sequence = NotificationSequence()
        _ = try sequence.update([provenanceSession("waiting", delegate: "delegate")], initial: true)
        let response = changed ? "New main response" : "Earlier main response"
        _ = try sequence.update([provenanceSession("waiting", response: response, delegate: "delegate")])
        let done = try sequence.update([provenanceSession("idle", response: response)])
        #expect(done.notices.map(\.kind) == (changed ? [.finished] : []))
    }
}

@Test func aNewMainResponseDuringAnIdleDelegateWaitIsStillNewsWhenItEnds() throws {
    var sequence = NotificationSequence()
    _ = try sequence.update([provenanceSession("idle", response: nil)], initial: true)
    _ = try sequence.update([provenanceSession("waiting", response: "New main response", delegate: "delegate")])
    _ = try sequence.update([provenanceSession("waiting", response: "New main response", delegate: "delegate")])
    #expect(try sequence.update([provenanceSession("idle", response: "New main response")]).notices.map(\.kind) == [.finished])
}

@Test func aLaterDelegateOnlyWaitDoesNotReuseAnEarlierMainTurnsProvenance() throws {
    var sequence = NotificationSequence()
    _ = try sequence.update([provenanceSession("working")])
    _ = try sequence.update([provenanceSession("waiting", delegate: "delegate")])
    #expect(try sequence.update([provenanceSession("idle")]).notices.map(\.kind) == [.finished])
    _ = try sequence.update([provenanceSession("waiting", delegate: "delegate")])
    #expect(try sequence.update([provenanceSession("idle")]).notices.isEmpty)
}

@Test func delegateWaitProvenanceIsScopedToItsMachineAndSession() throws {
    var sequence = NotificationSequence()
    _ = try sequence.update([provenanceSession("working")], machine: "first")
    _ = try sequence.update([provenanceSession("waiting", delegate: "delegate")], machine: "first")
    _ = try sequence.update([provenanceSession("waiting", delegate: "delegate")], machine: "second", initial: true)
    #expect(try sequence.update([provenanceSession("idle")], machine: "second").notices.isEmpty)
    _ = try sequence.update([provenanceSession("waiting", delegate: "delegate"), provenanceSession("waiting", delegate: "delegate", id: "other")], machine: "first")
    let done = try sequence.update([provenanceSession("idle"), provenanceSession("idle", id: "other")], machine: "first")
    #expect(done.notices.map(\.sessionID) == ["session"])
}

@Test func removedClearedAndSignedOutSessionsForgetTheirWaitProvenance() throws {
    for reset in ["remove", "session", "machine", "account"] {
        var sequence = NotificationSequence()
        _ = try sequence.update([provenanceSession("working")])
        _ = try sequence.update([provenanceSession("waiting", delegate: "delegate")])
        switch reset {
        case "remove": _ = sequence.update([])
        case "session": sequence.clear(machine: "machine", session: "session")
        case "machine": sequence.clear(machine: "machine")
        default: sequence = NotificationSequence()
        }
        _ = try sequence.update([provenanceSession("waiting", delegate: "delegate")], initial: true)
        #expect(try sequence.update([provenanceSession("idle")]).notices.isEmpty)
    }
}

@Test func aReconnectCannotRestoreAClearedWorkingTurnFromItsPreviousSnapshot() throws {
    for sessionOnly in [false, true] {
        var sequence = NotificationSequence()
        let oldWorking = try provenanceSession("working")
        _ = sequence.update([oldWorking])
        sequence.clear(machine: "machine", session: sessionOnly ? oldWorking.id : nil)
        // The provider still owns oldWorking; the planner uses only its cleared snapshot.
        #expect(sequence.history.sessions["machine"]?[oldWorking.id] == nil)
        _ = try sequence.update([provenanceSession("waiting", delegate: "delegate")], initial: true)
        #expect(try sequence.update([provenanceSession("idle")]).notices.isEmpty)
    }
}
