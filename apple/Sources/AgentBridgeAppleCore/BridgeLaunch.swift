import Foundation

public struct BridgeLauncher: Identifiable, Sendable, Equatable {
    public let id: String
    public let label: String
    public let kind: String
    public let models: [String]
    public let efforts: [String]
    public let contexts: [String]
    public let profiles: [String]
    public let defaults: JSONValue?
    public let attachments: Bool
    public let needsPrompt: Bool
    public var supported: Bool {
        ["agency", "copilot", "claude", "codex"].contains(id)
    }

    public init?(json: JSONValue) {
        guard let id = json["id"]?.string, !id.isEmpty else { return nil }
        self.id = id
        label = json["label"]?.string ?? id
        kind = json["kind"]?.string ?? "unknown"
        models = json["models"]?.array?.compactMap(\.string) ?? []
        efforts = json["efforts"]?.array?.compactMap(\.string) ?? []
        contexts = json["contexts"]?.array?.compactMap(\.string) ?? []
        profiles = json["profiles"]?.array?.compactMap(\.string) ?? []
        defaults = json["defaults"]
        attachments = json["attachments"]?.bool == true
        needsPrompt = json["needsPrompt"]?.bool == true
    }
}

public struct BridgeLaunchWorkspace: Identifiable, Sendable, Equatable {
    public var id: String {
        label
    }

    public let label: String
    public let path: String
    public let isolate: Bool

    public init?(json: JSONValue) {
        guard let label = json["label"]?.string, !label.isEmpty, let path = json["path"]?.string else { return nil }
        self.label = label
        self.path = path
        isolate = json["isolate"]?.bool == true
    }
}

public struct BridgeResumableSession: Identifiable, Sendable, Equatable {
    public let id: String
    public let agent: String
    public let launcher: String
    public let title: String
    public let cwd: String?
    public let workspace: String?
    public let updatedAt: String?

    public init?(json: JSONValue) {
        guard let id = json["id"]?.string, !id.isEmpty else { return nil }
        self.id = id
        agent = json["agent"]?.string ?? "unknown"
        launcher = json["launcher"]?.string ?? agent
        title = json["title"]?.string ?? id
        cwd = json["cwd"]?.string
        workspace = json["workspace"]?.string
        updatedAt = json["updatedAt"]?.string
    }
}

public struct BridgeLaunchOptions: Sendable, Equatable {
    public let agents: [BridgeLauncher]
    public let workspaces: [BridgeLaunchWorkspace]
    public let resumable: [BridgeResumableSession]
    public let defaults: JSONValue?
    public let busy: String?

    public init(json: JSONValue) {
        agents = (json["agents"]?.array ?? []).compactMap(BridgeLauncher.init)
        workspaces = (json["workspaces"]?.array ?? []).compactMap(BridgeLaunchWorkspace.init)
        resumable = (json["resumable"]?.array ?? []).compactMap(BridgeResumableSession.init)
        defaults = json["defaults"]
        busy = json["busy"]?.string
    }

    public static func operation(capabilities: Set<String>) throws -> BridgeOperation {
        try BridgeOperationValidation.require("sessions.launch", in: capabilities)
        return BridgeOperation(op: "launch.options")
    }
}

public struct BridgeLaunchDraft: Sendable, Equatable {
    public var agent: String?
    public var workspace: String?
    public var resume: String?
    public var profile: String?
    public var model: String?
    public var effort: String?
    public var context: String?
    public var permissions: String?
    public var prompt = ""
    public var attachments: [UUID] = []

    public init() {}

    public func operation(options: BridgeLaunchOptions, capabilities: Set<String>, requestID: UUID = UUID()) throws -> BridgeOperation {
        try BridgeOperationValidation.require("sessions.launch", in: capabilities)
        if let busy = options.busy, !busy.isEmpty { // host text: never shown, only its presence counts
            throw BridgeOperationError("This machine is busy. Try again in a moment.")
        }
        guard resume == nil || workspace == nil else { throw BridgeOperationError("A resumed session reopens in its own folder. Choose a workspace or a session to resume.") }
        let resumed = options.resumable.first { $0.id == resume }
        if resume != nil, resumed == nil {
            throw BridgeOperationError("This session is no longer available to resume.")
        }
        let launcherID = agent ?? resumed?.launcher ?? options.defaults?["agent"]?.string
        guard let launcher = options.agents.first(where: { $0.id == launcherID }), launcher.supported else {
            throw BridgeOperationError("Choose a launcher offered by this machine.")
        }
        if let resumed, resumed.launcher != launcher.id {
            throw BridgeOperationError("Resume with the launcher listed for this session.")
        }
        if resume == nil {
            let workspaceLabel = workspace ?? options.defaults?["workspace"]?.string
            guard options.workspaces.contains(where: { $0.label == workspaceLabel }) else {
                throw BridgeOperationError("Choose a workspace approved on this machine.")
            }
        }
        for (_, value, offered) in [("model", model, launcher.models), ("effort", effort, launcher.efforts), ("context", context, launcher.contexts)] {
            if let value, !value.isEmpty {
                let allowed = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-:[]")
                guard !value.hasPrefix("-"), value.unicodeScalars.allSatisfy({ allowed.contains($0) }), offered.contains(value) else {
                    throw BridgeOperationError("Choose a model, effort or context offered by this launcher.")
                }
            }
        }
        if let profile, launcher.id != "agency" || !launcher.profiles.contains(profile) {
            throw BridgeOperationError("Choose an Agency profile offered by this machine.")
        }
        if let permissions, !["ask", "allowAll"].contains(permissions) {
            throw BridgeOperationError("Choose Ask or Allow all permissions.")
        }
        try BridgeOperationValidation.prompt(prompt, allowEmpty: resume != nil || !launcher.needsPrompt)
        try BridgeOperationValidation.attachments(attachments, capabilities: capabilities)
        if !attachments.isEmpty, !launcher.attachments {
            throw BridgeOperationError("This launcher cannot take opening attachments.")
        }
        var data: [String: JSONValue] = ["request": .string(requestID.uuidString.lowercased())]
        for (name, value) in [("agent", agent), ("workspace", workspace), ("resume", resume), ("profile", profile), ("model", model), ("effort", effort), ("context", context), ("permissions", permissions)] {
            if let value {
                guard value.utf8.count <= 256, !value.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
                    throw BridgeOperationError("A launch option is too long or contains control characters.")
                }
                data[name] = .string(value)
            }
        }
        if !prompt.isEmpty {
            data["prompt"] = .string(prompt)
        }
        if !attachments.isEmpty {
            data["attachments"] = BridgeOperationValidation.attachmentReferences(attachments)
        }
        try BridgeOperationValidation.encodedSize(data, maximum: 20 * 1024)
        return BridgeOperation(op: "session.launch", data: data, requestID: requestID)
    }
}

public struct BridgeLaunchResult: Sendable, Equatable {
    public enum Outcome: String, Sendable { case started, unconfirmed, failed, busy, refused }
    public let outcome: Outcome
    public let reason: String?
    public let sessionID: String?
    public let launched: JSONValue?
    public let trust: JSONValue?
    public var allowsNewAttempt: Bool {
        outcome == .failed || outcome == .busy || outcome == .refused
    }

    public init(json: JSONValue) {
        outcome = Outcome(rawValue: json["outcome"]?.string ?? "") ?? .unconfirmed
        reason = json["reason"]?.string
        sessionID = json["session"]?.string
        launched = json["launched"]
        trust = json["trust"]
    }
}
