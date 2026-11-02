import Foundation

public enum BridgeTransfer {
    public static let timeout: Duration = .seconds(12 * 60)

    /// The source key must come from its authenticated channel, and sourceSession from
    /// that source's current launch.options. The prepared operation keeps the one signed
    /// command across a lost response; an uncertain launch is never recreated here.
    public static func launch(draft: BridgeLaunchDraft, sourceSession: BridgeResumableSession, sourcePublicKey: Data,
                              targetMachineID: String, signerPrivateKey: Data, options: BridgeLaunchOptions,
                              capabilities: Set<String>, sourceCapabilities: Set<String>, isLocal: Bool,
                              requestID: UUID = UUID(), now: Date = Date()) throws -> BridgeOperation
    {
        try BridgeOperationValidation.require("sessions.launch", in: capabilities)
        try BridgeOperationValidation.require("sessions.transfer", in: capabilities)
        try BridgeOperationValidation.require("sessions.transfer.serve", in: sourceCapabilities)
        guard !isLocal else { throw BridgeOperationError("Session transfers require a relay connection.") }
        guard draft.resume == nil else { throw BridgeOperationError("Choose a local resume or a transfer, not both.") }
        guard ["copilot", "claude", "codex"].contains(sourceSession.agent),
              let agent = draft.agent, let launcher = options.agents.first(where: { $0.id == agent }), launcher.supported,
              launcher.kind == sourceSession.agent else { throw BridgeOperationError("Choose an installed launcher of the transferred session's agent kind.") }
        guard let workspace = draft.workspace, !workspace.isEmpty else { throw BridgeOperationError("Choose the workspace where this transferred session will open.") }
        let sourceID = BridgeSignedCommand.deviceID(sourcePublicKey)
        guard sourceID != targetMachineID else { throw BridgeOperationError("Resume this machine's own session directly.") }
        // A transfer resumes a session, so even launchers needing a first message for a
        // new session accept an empty prompt. All other offered choices remain intact.
        let launcherJSON: JSONValue = .object([
            "id": .string(launcher.id), "label": .string(launcher.label), "kind": .string(launcher.kind),
            "models": .array(launcher.models.map(JSONValue.string)), "efforts": .array(launcher.efforts.map(JSONValue.string)),
            "contexts": .array(launcher.contexts.map(JSONValue.string)), "profiles": .array(launcher.profiles.map(JSONValue.string)),
            "attachments": .bool(launcher.attachments), "needsPrompt": .bool(false),
        ])
        var offered: [String: JSONValue] = ["agents": .array([launcherJSON]), "workspaces": .array(options.workspaces.map {
            .object(["label": .string($0.label), "path": .string($0.path), "isolate": .bool($0.isolate)])
        })]
        if let defaults = options.defaults {
            offered["defaults"] = defaults
        }
        if let busy = options.busy {
            offered["busy"] = .string(busy)
        }
        let prepared = try draft.operation(options: BridgeLaunchOptions(json: .object(offered)), capabilities: capabilities, requestID: requestID)
        let command = try BridgeSignedCommand.sign(action: .transfer, targetMachinePublicKey: sourcePublicKey, targetSession: sourceSession.id,
                                                   to: targetMachineID, signerPrivateKey: signerPrivateKey, requestID: requestID, now: now)
        var data = prepared.data
        data["transfer"] = .object(["from": .string(sourceID), "session": .string(sourceSession.id), "command": command.json])
        try BridgeOperationValidation.encodedSize(data, maximum: 20 * 1024)
        return BridgeOperation(op: "session.launch", data: data, requestID: requestID)
    }
}
