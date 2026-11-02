#if DEBUG
    import AgentBridgeAppleCore
    import Foundation

    /// Disposable in-process UI fixture. It opens no socket and never reaches a host.
    actor PreviewHost {
        private let stream = AsyncThrowingStream<BridgeEnvelope, any Error>.makeStream()
        private var sequence = 0
        private var sessions: [JSONValue] = {
            let json = #"""
            [
              {"id":"preview-review","title":"Review the Apple clients","agent":"claude","status":"waiting","repository":"example/agent-bridge","branch":"apple-parity","updatedAt":"2026-11-02T21:00:00Z","activity":{"response":"## Ready for review\n\nThe native clients now share **encrypted connections**, session actions, and machine views.\n\n- macOS workbench\n- iPhone and iPad layouts\n- Capability-gated controls"},"question":{"id":"preview-question","kind":"form","message":"Which client should receive the next visual review?","fields":[{"name":"platform","title":"Client","type":"choice","required":true,"allowOther":true,"options":[{"value":"mac","label":"macOS (Recommended)"},{"value":"ipad","label":"iPadOS"}]}]}},
              {"id":"preview-build","title":"Build protocol compatibility tests","agent":"codex","status":"working","repository":"example/agent-bridge","updatedAt":"2026-11-02T20:58:00Z","activity":{"response":"Checking encrypted channels and request ordering.","currentTool":{"name":"swift test","summary":"Running protocol fixtures","status":"running"}}}
            ]
            """#
            return (try? JSONDecoder().decode([JSONValue].self, from: Data(json.utf8))) ?? []
        }()

        func connect() -> HostTransport {
            HostTransport(messages: stream.stream, send: { request, _ in try await self.respond(request) }, close: { await self.close() }, peerKey: nil)
        }

        private func close() {
            stream.continuation.finish()
        }

        private func emit(_ event: String, _ data: JSONValue) throws {
            sequence += 1
            try stream.continuation.yield(envelope(.object(["ev": .string(event), "seq": .number(Double(sequence)), "data": data])))
        }

        private func envelope(_ json: JSONValue) throws -> BridgeEnvelope {
            try JSONDecoder().decode(BridgeEnvelope.self, from: JSONEncoder().encode(json))
        }

        private func respond(_ request: BridgeRequest) throws -> BridgeEnvelope {
            let result: JSONValue
            switch request.op {
            case "hello": result = .object(["protocol": .number(1), "machine": .object(["id": .string("preview"), "name": .string("Demo machine"), "instance": .string("preview-only")]),
                                            "capabilities": .array(["sessions.read", "sessions.answer", "sessions.reply", "sessions.stop", "sessions.launch", "usage"].map(JSONValue.string))])
            case "sessions.subscribe": result = .object([:])
            case "usage.get", "usage.subscribe": result = .object(["agents": .array([])])
            case "session.get":
                let base = sessions.first { $0["id"] == request.data["id"] } ?? .object([:])
                if case var .object(fields) = base {
                    fields["tools"] = .array([.object(["id": .string("preview-tool"), "name": .string("swift test"), "status": .string("completed"), "summary": .string("Protocol fixtures passed")])])
                    result = .object(fields)
                } else {
                    result = base
                }
            case "launch.options": result = .object(["agents": .array([.object(["id": .string("codex"), "kind": .string("codex"), "label": .string("Codex"), "models": .array([.string("default")])])]),
                                                     "workspaces": .array([.object(["label": .string("Example project"), "path": .string("/example/project")])]),
                                                     "defaults": .object(["agent": .string("codex"), "workspace": .string("Example project")])])
            case "session.launch": result = .object(["outcome": .string("started"), "session": .string("preview-new"), "reason": .string("Simulated launch. No process was started.")])
            case "session.stop":
                result = request.data["confirm"] == nil ? .object(["outcome": .string("confirm"), "confirm": .string("preview-confirm"), "confirmSeconds": .number(30)]) : .object(["outcome": .string("ended"), "reason": .string("Simulated stop. No process was stopped.")])
            case "session.answer", "session.reply": result = .object(["outcome": .string("delivered"), "reason": .string("Simulated delivery. No keys were sent.")])
            default: result = .null
            }
            let response = try envelope(.object(["re": .number(Double(request.id)), "ok": .bool(true), "result": result]))
            stream.continuation.yield(response)
            if request.op == "sessions.subscribe" {
                for session in sessions {
                    try emit("session.upsert", session)
                }
                try emit("sessions.synced", .object([:]))
            }
            if request.op == "session.answer", let index = sessions.firstIndex(where: { $0["id"] == request.data["id"] }), case var .object(fields) = sessions[index] {
                fields["question"] = .null; fields["status"] = .string("working")
                sessions[index] = .object(fields)
                try emit("session.upsert", sessions[index])
            }
            if request.op == "session.launch" {
                let session: JSONValue = .object(["id": .string("preview-new"), "title": .string("New demo session"),
                                                  "agent": .string("codex"), "status": .string("idle"),
                                                  "repository": .string("example/project"),
                                                  "activity": .object(["userMessage": request.data["prompt"] ?? .string(""),
                                                                       "response": .string("This is a **simulated session** for reviewing the client. No process was started.")])])
                sessions.removeAll { $0["id"] == .string("preview-new") }
                sessions.append(session)
                try emit("session.upsert", session)
            }
            if request.op == "session.stop", request.data["confirm"] != nil, let id = request.data["id"]?.string {
                sessions.removeAll { $0["id"]?.string == id }
                try emit("session.remove", .object(["id": .string(id)]))
            }
            return response
        }
    }
#endif
