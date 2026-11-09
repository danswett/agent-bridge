import AgentBridgeAppleCore
import SwiftUI

struct FolderTrustView: View {
    let question: BridgeTrustQuestion
    let host: HostStore
    let store: BridgeStore
    @State private var error: String?
    @State private var preparing = false
    private var via: HostStore? {
        store.remoteHost(for: host)
    }

    private var slot: String {
        "trust:\(question.id)"
    }

    private var attempt: MutationAttempt? {
        via?.attempts[slot]
    }

    var body: some View {
        Section("\(question.label) asks to trust a folder") {
            if let workspace = question.workspace {
                LabeledContent("Workspace", value: workspace)
            }
            Text(question.folder).font(.body.monospaced()).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
            if let rootNote = question.rootNote {
                Text(rootNote).fixedSize(horizontal: false, vertical: true)
            }
            if let root = question.root {
                Text(root).font(.body.monospaced()).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
            }
            if let warning = question.warning {
                Text(warning).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
            }
            if !question.answerable || via?.can("launch.trust") != true {
                Text(question.atMachine ?? "Answer this folder-trust question at the machine.").foregroundStyle(.secondary)
            } else if attempt?.uncertain != true && (attempt?.result == nil || attempt?.result?["outcome"]?.string == "refused") {
                Button("Trust and Continue") { answer(.trust) }.disabled(preparing || attempt?.isSending == true)
                Button("Quit", role: .destructive) { answer(.quit) }.disabled(preparing || attempt?.isSending == true)
            }
            if preparing || attempt?.isSending == true {
                ProgressView("Waiting for the machine…")
            }
            if let attempt {
                if let result = attempt.result {
                    Text(result["outcome"]?.string?.capitalized ?? "Unconfirmed")
                    if let reason = result["reason"]?.string {
                        Text(reason)
                    }
                }
                if let failure = attempt.error {
                    Text(failure)
                }
                if attempt.canRetry, let via {
                    Button("Check Answer Again") { Task { await via.retry(slot) } }
                        .disabled(!via.isLive || via.instance != attempt.hostInstance)
                }
            }
            if let error {
                Text(error).foregroundStyle(.red)
            }
        }
        .onChange(of: attempt?.result, initial: true) {
            if let via, let result = attempt?.result {
                store.reconcileTrust(questionID: question.id, on: via, result: result)
            }
        }
    }

    private func answer(_ choice: BridgeTrustQuestion.Choice) {
        guard let via else { return }
        preparing = true
        Task {
            defer { preparing = false }
            do {
                let connection = via.connectionID
                let current = try await via.call("launch.options", timeout: .seconds(90), onlyOn: connection)
                guard let raw = current["trust"]?.array?.first(where: { $0["id"]?.string == question.id }),
                      let fresh = BridgeTrustQuestion(json: raw), fresh == question,
                      let peer = via.peerKey else { throw BridgeOperationError("This folder-trust question changed. Refresh launch options and review it again.") }
                let operation = try fresh.answer(choice: choice, currentQuestionID: question.id, machinePublicKey: peer,
                                                 signerPrivateKey: store.signingKey(for: via), capabilities: via.capabilities, isLocal: false)
                guard connection == via.connectionID else { throw RelayError.disconnected }
                await via.perform(operation, slot: slot)
                if let result = via.attempts[slot]?.result {
                    store.reconcileTrust(questionID: question.id, on: via, result: result)
                }
            } catch { self.error = BridgeShownError.sentence(for: error) }
        }
    }
}
