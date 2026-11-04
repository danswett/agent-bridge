import AgentBridgeAppleCore
@testable import AgentBridgeClientState
import Foundation
import Testing

private func responseSession(status: String = "working", response: String? = nil,
                             prompt: String = "A task", turns: Int = 1, truncated: Bool = false,
                             question: Bool = false, delegateResponse: String? = nil) throws -> BridgeSession
{
    var activity: [String: JSONValue] = [
        "userMessage": .string(prompt), "turnCount": .number(Double(turns)),
    ]
    if let delegateResponse {
        activity["response"] = .string(delegateResponse)
        activity["source"] = .object(["response": .string("delegate-1")])
        activity["own"] = .object([
            "response": response.map(JSONValue.string) ?? .null,
            "responseTruncated": .bool(truncated),
        ])
    } else {
        activity["response"] = response.map(JSONValue.string)
        activity["responseTruncated"] = .bool(truncated)
    }
    return try #require(BridgeSession(json: .object([
        "id": .string("session"), "agent": .string("copilot"), "status": .string(status),
        "activity": .object(activity),
        "question": question ? .object(["id": .string("question")]) : .null,
    ])))
}

@Test func workingWithoutAResponseShowsProgressButExistingFirstSnapshotTextDoesNot() throws {
    for response in [nil, "", " \n "] {
        var progress = ResponseProgress()
        try progress.receive(responseSession(response: response))
        #expect(progress.awaitingResponse)
    }
    var progress = ResponseProgress()
    try progress.receive(responseSession(response: "Already responding"))
    #expect(!progress.awaitingResponse)
}

@Test func aNewObservedTurnCanWaitWhileTheHostRetainsThePreviousResponse() throws {
    var progress = ResponseProgress()
    try progress.receive(responseSession(status: "idle", response: "Previous result"))
    let next = try responseSession(response: "Previous result", prompt: "Next task")
    progress.receive(next)
    #expect(progress.awaitingResponse)
    #expect(next.response == "Previous result")
    progress.receive(next)
    #expect(progress.awaitingResponse)
}

@Test func freshResponseStopsProgressEvenWhileTheAgentContinuesWorking() throws {
    var progress = ResponseProgress()
    try progress.receive(responseSession(status: "idle", response: "Old result"))
    try progress.receive(responseSession(response: "Old result", prompt: "New task"))
    #expect(progress.awaitingResponse)
    try progress.receive(responseSession(response: "New response", prompt: "New task"))
    #expect(!progress.awaitingResponse)
    try progress.receive(responseSession(response: "New response", prompt: "New task"))
    #expect(!progress.awaitingResponse)
}

@Test func coalescedPromptAndResponseDoNotFlashAWorkingPlaceholder() throws {
    var progress = ResponseProgress()
    try progress.receive(responseSession(status: "idle", response: "Old"))
    try progress.receive(responseSession(response: "New", prompt: "New task", turns: 2))
    #expect(!progress.awaitingResponse)
}

@Test func aNewPromptOrTurnCountCanStartProgressWithoutAnObservedIdleTransition() throws {
    var progress = ResponseProgress()
    try progress.receive(responseSession(response: "Current text"))
    try progress.receive(responseSession(response: "Current text", prompt: "A different prompt"))
    #expect(progress.awaitingResponse)
    try progress.receive(responseSession(response: "Answer", prompt: "A different prompt"))
    #expect(!progress.awaitingResponse)
    try progress.receive(responseSession(response: "Answer", prompt: "A different prompt", turns: 2))
    #expect(progress.awaitingResponse)
}

@Test(arguments: ["idle", "waiting", "ending"])
func nonworkingStatesDoNotShowProgress(status: String) throws {
    var progress = ResponseProgress()
    try progress.receive(responseSession())
    #expect(progress.awaitingResponse)
    try progress.receive(responseSession(status: status))
    #expect(!progress.awaitingResponse)
}

@Test func aQuestionStopsTheWorkingIndicatorUntilWorkResumes() throws {
    var progress = ResponseProgress()
    try progress.receive(responseSession())
    try progress.receive(responseSession(status: "waiting", question: true))
    #expect(!progress.awaitingResponse)
    try progress.receive(responseSession())
    #expect(progress.awaitingResponse)
    try progress.receive(responseSession(question: true))
    #expect(!progress.awaitingResponse)
}

@Test func delegateOutputDoesNotFinishTheMainAgentsPendingResponse() throws {
    var progress = ResponseProgress()
    try progress.receive(responseSession(status: "idle", response: "Old main response"))
    try progress.receive(responseSession(response: "Old main response", prompt: "New task", delegateResponse: "Delegate result"))
    #expect(progress.awaitingResponse)
    try progress.receive(responseSession(response: "New main response", prompt: "New task", delegateResponse: "Delegate result"))
    #expect(!progress.awaitingResponse)
}

@Test func hostOmittedResponseIsNotMistakenForWorkThatHasNotReturnedAnything() throws {
    var progress = ResponseProgress()
    try progress.receive(responseSession(truncated: true, delegateResponse: "Delegate result"))
    #expect(!progress.awaitingResponse)
}

private actor WorkingResponseHost {
    let stream = AsyncThrowingStream<BridgeEnvelope, any Error>.makeStream()
    private var sequence = 0

    func connect() -> HostTransport {
        HostTransport(messages: stream.stream, send: { request, _ in try await self.send(request) },
                      close: { await self.close() }, peerKey: nil)
    }

    func close() {
        stream.continuation.finish(throwing: RelayError.disconnected)
    }

    func respond() throws {
        try update("A new response")
    }

    private func update(_ response: String?) throws {
        sequence += 1
        var activity: [String: JSONValue] = ["userMessage": .string("Task"), "turnCount": .number(1)]
        activity["response"] = response.map(JSONValue.string)
        try stream.continuation.yield(envelope([
            "ev": .string("session.upsert"), "seq": .number(Double(sequence)),
            "data": .object(["id": .string("session"), "agent": .string("copilot"),
                             "status": .string("working"), "activity": .object(activity)]),
        ]))
    }

    private func send(_ request: BridgeRequest) throws -> BridgeEnvelope {
        let result: JSONValue = request.op == "hello" ? .object([
            "protocol": .number(1), "capabilities": .array([.string("sessions.read")]),
            "machine": .object(["id": .string("fixture"), "instance": .string("fixture-run")]),
        ]) : .object([:])
        let response = try envelope(["re": .number(Double(request.id)), "ok": .bool(true), "result": result])
        stream.continuation.yield(response)
        if request.op == "sessions.subscribe" {
            try update(nil)
            sequence += 1
            try stream.continuation.yield(envelope(["ev": .string("sessions.synced"), "seq": .number(Double(sequence)), "data": .object([:])]))
        }
        return response
    }

    private func envelope(_ fields: [String: JSONValue]) throws -> BridgeEnvelope {
        try JSONDecoder().decode(BridgeEnvelope.self, from: JSONEncoder().encode(JSONValue.object(fields)))
    }
}

@MainActor @Test(.timeLimit(.minutes(1)))
func liveHostSnapshotsDriveProgressAndNewTextStopsIt() async throws {
    let fixture = WorkingResponseHost()
    let host = HostStore(id: "fixture", name: "Fixture", isLocal: false) { await fixture.connect() }
    host.start()
    defer { host.stop() }
    for _ in 0 ..< 200 where !host.isAwaitingResponse(for: "session") {
        try await Task.sleep(for: .milliseconds(5))
    }
    #expect(host.isAwaitingResponse(for: "session"))
    #expect(!host.isAwaitingResponse(for: "another-session"))
    try await fixture.respond()
    for _ in 0 ..< 200 where host.isAwaitingResponse(for: "session") {
        try await Task.sleep(for: .milliseconds(5))
    }
    #expect(!host.isAwaitingResponse(for: "session"))
    host.stop()
    #expect(!host.isAwaitingResponse(for: "session"))
}

@MainActor @Test(.timeLimit(.minutes(1)))
func disconnectStopsProgressEvenWhenNoResponseHasArrived() async throws {
    let fixture = WorkingResponseHost()
    let host = HostStore(id: "fixture", name: "Fixture", isLocal: false) { await fixture.connect() }
    host.start()
    defer { host.stop() }
    for _ in 0 ..< 200 where !host.isAwaitingResponse(for: "session") {
        try await Task.sleep(for: .milliseconds(5))
    }
    #expect(host.isAwaitingResponse(for: "session"))
    host.stop()
    #expect(!host.isAwaitingResponse(for: "session"))
}
