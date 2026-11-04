#if DEBUG
    import AgentBridgeAppleCore
    import Foundation

    /// Disposable in-process UI fixture. It opens no socket and never reaches a host.
    actor PreviewHost {
        private let stream = AsyncThrowingStream<BridgeEnvelope, any Error>.makeStream()
        private var sequence = 0
        private let delegates: [JSONValue] = {
            let json = #"""
            [
              {"id":"preview-layout","kind":"background","name":"Review native layouts","task":"Check the Mac, iPhone and iPad workbenches.","model":"Demo model","status":"working","startedAt":"2026-11-02T21:00:00Z","line":"Checking navigation and readable tool rows.","response":"## Layout review\n\nThe clients use native navigation and shared session state.\n\n| Client | Review |\n| --- | --- |\n| macOS | Sidebar and keyboard controls |\n| iPhone | Compact navigation |\n| iPad | Sidebar and inspector |","reasoning":"Keep the **main agent** and each delegate clearly attributed.","currentTool":{"id":"layout-current","name":"inspect","summary":"Reviewing the compact layout","status":"running","startedAt":"2026-11-02T21:01:00Z"},"history":[{"id":"layout-test","name":"swift test","summary":"Presentation fixtures passed","status":"done","startedAt":"2026-11-02T21:00:00Z","endedAt":"2026-11-02T21:00:03Z"}]},
              {"id":"preview-nested","parent":"preview-layout","kind":"task","name":"Check accessibility labels","task":"Review control labels and delegate attribution.","status":"ended","outcome":"done","startedAt":"2026-11-02T21:00:10Z","endedAt":"2026-11-02T21:00:20Z","line":"Every delegate stays explicitly labelled.","response":"The fixture's controls have descriptive labels. Full VoiceOver acceptance remains pending."},
              {"id":"preview-crypto","kind":"task","name":"Check protocol vectors","task":"Compare encrypted transport fixtures.","status":"ended","outcome":"done","startedAt":"2026-11-02T20:59:00Z","endedAt":"2026-11-02T21:00:00Z","line":"Protocol fixtures passed.","response":"The local cryptographic vectors match the upstream .NET and WebCrypto fixtures. This is a **demo response**."}
            ]
            """#
            return (try? JSONDecoder().decode([JSONValue].self, from: Data(json.utf8))) ?? []
        }()

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

        private func objectFields(_ value: JSONValue?) -> [String: JSONValue] {
            guard let value, case let .object(fields) = value else { return [:] }
            return fields
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
                if let agent = request.data["agent"]?.string {
                    result = base["id"]?.string == "preview-build" ? delegates.first { $0["id"]?.string == agent } ?? .null : .null
                } else if case var .object(fields) = base {
                    var activity = objectFields(fields["activity"])
                    activity["history"] = .array([.object(["id": .string("preview-tool"), "name": .string("swift test"), "status": .string("done"),
                                                           "summary": .string("Protocol fixtures passed"), "startedAt": .string("2026-11-02T21:00:00Z"),
                                                           "endedAt": .string("2026-11-02T21:00:03Z")])])
                    fields["activity"] = .object(activity)
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
                if let index = sessions.firstIndex(where: { $0["id"]?.string == "preview-build" }), case var .object(fields) = sessions[index] {
                    fields["subagents"] = .array(delegates.map { full in
                        .object(objectFields(full).filter { !["response", "responseTruncated", "reasoning", "history"].contains($0.key) })
                    })
                    var activity = objectFields(fields["activity"])
                    activity["background"] = .object(["agents": .number(1), "shells": .number(1)])
                    fields["activity"] = .object(activity)
                    sessions[index] = .object(fields)
                }
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
