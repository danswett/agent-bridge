import AgentBridgeAppleCore
import Foundation

/// Explicit, offline preview data. Never merged with a host's live sessions.
enum SampleSessions {
    static var sessions: [BridgeSession] {
        let source = #"""
        [
          {"id":"sample-review","agent":"claude","title":"Review the Apple client","repository":"example/agent-bridge","branch":"feature/apple-clients","model":"Claude","status":"waiting","activity":{"userMessage":"Review the connection lifecycle and session updates.","response":"The connection now handles live updates. The remaining decision is how to present a disconnected host."},"question":{"id":"sample-question","kind":"form","message":"Keep the last received sessions visible when a host disconnects?","answerAtMachine":true}},
          {"id":"sample-build","agent":"codex","title":"Build the session workbench","repository":"example/agent-bridge","status":"working","activity":{"userMessage":"Build the shared session interface for Mac, iPhone, and iPad.","response":"The shared interface is in place. I’m checking navigation at each window size.","currentTool":{"id":"sample-tool","name":"xcodebuild","summary":"Building the iPad app","status":"running"}}},
          {"id":"sample-delegates","agent":"copilot","title":"Verify protocol compatibility","status":"idle","driver":"agent","activity":{"response":"Framing checks are complete. Two delegates are checking the event stream.","background":{"agents":2,"shells":0}},"subagents":[{"id":"sample-d1","name":"Snapshot consistency","status":"working","line":"Checking replacement snapshots and sequence gaps."},{"id":"sample-d2","name":"Connection recovery","status":"working","line":"Checking cancellation and reconnect behavior."}]}
        ]
        """#
        guard let values = try? JSONDecoder().decode([JSONValue].self, from: Data(source.utf8)) else { return [] }
        return values.compactMap(BridgeSession.init(json:))
    }
}
