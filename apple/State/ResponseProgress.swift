import AgentBridgeAppleCore
import Foundation

/// Protocol v1 retains the last response across prompts and has no response turn ID.
/// Compare observed turns; a first snapshot with text is not proof that the text is stale.
struct ResponseProgress {
    private var previous: BridgeSession?
    private(set) var awaitingResponse = false

    mutating func receive(_ session: BridgeSession) {
        defer { previous = session }
        guard session.status == "working", session.question == nil else {
            awaitingResponse = false
            return
        }
        let hasResponse = session.response?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == false
            || session.responseTruncated
        guard let previous, previous.id == session.id else {
            awaitingResponse = !hasResponse
            return
        }
        let newResponse = session.response != previous.response
            || session.responseTruncated != previous.responseTruncated
        let newTurn = previous.status != "working"
            || session.activity?["userMessage"] != previous.activity?["userMessage"]
            || session.activity?["turnCount"] != previous.activity?["turnCount"]
        if newResponse, hasResponse {
            awaitingResponse = false
        } else if newTurn || !hasResponse {
            awaitingResponse = true
        }
    }
}
