@testable import AgentBridgeAppleCore
import CryptoKit
import Foundation
import Testing

private func commandHex(_ string: String) -> Data {
    Data(stride(from: 0, to: string.count, by: 2).map { offset in
        let start = string.index(string.startIndex, offsetBy: offset)
        return UInt8(string[start ..< string.index(start, offsetBy: 2)], radix: 16)!
    })
}

// Public, fixed interoperability fixtures from SignedCommandTests.cs and the PWA.
private let commandPhoneKey = commandHex("905900d4260fa339e62fdd5e34ca9db632ff099271432b0b957d44c389d4576e")
private let commandMachineKey = commandHex("5f659de036a5d83c281f6c80a44633fc4cae31a37a140c73c21bff1d917b3597")
private let commandRequest = UUID(uuidString: "7d0f3a2e-1c4b-4d5e-8f60-112233445566")!
private let commandNow = Date(timeIntervalSince1970: 1_793_300_000)

@Test func signedTransferMatchesTheHostAndPWAByteForByte() throws {
    let machine = try P256.KeyAgreement.PrivateKey(rawRepresentation: commandMachineKey)
    let phone = try P256.KeyAgreement.PrivateKey(rawRepresentation: commandPhoneKey)
    let command = try BridgeSignedCommand.sign(action: .transfer, targetMachinePublicKey: machine.publicKey.x963Representation,
                                               targetSession: "0f3c4d5e-6a7b-4c8d-9e0f-a1b2c3d4e5f6", to: "00112233445566778899aabbccddeeff", signerPrivateKey: commandPhoneKey, requestID: commandRequest, now: commandNow)
    #expect(command.mac == "_PJ9LEydaL4_cLc4dUQ-wkKfhFj1DfQi-9K7Bl_0fxs")
    let body = try #require(BridgeBase64URL.decode(command.body))
    let key = try BridgeSignedCommand.deriveKey(privateKey: commandMachineKey, peerPublicKey: phone.publicKey.x963Representation,
                                                signerID: BridgeSignedCommand.deviceID(phone.publicKey.x963Representation), machineID: BridgeSignedCommand.deviceID(machine.publicKey.x963Representation))
    #expect(try HMAC<SHA256>.isValidAuthenticationCode(#require(BridgeBase64URL.decode(command.mac)), authenticating: body, using: key))
    #expect(try !HMAC<SHA256>.isValidAuthenticationCode(#require(BridgeBase64URL.decode(command.mac)), authenticating: body + Data([32]), using: key))
    #expect(command.expiresAt.timeIntervalSince(commandNow) == 300)
}

@Test func folderTrustSignatureBindsTheChoiceAndDisplayedQuestion() throws {
    let machine = try P256.KeyAgreement.PrivateKey(rawRepresentation: commandMachineKey).publicKey.x963Representation
    let question = try #require(BridgeTrustQuestion(json: .object(["id": .string("5d0c9f3e-2b1a-4c8d-9e7f-6a5b4c3d2e1f"), "folder": .string("/workspace/bridge"), "warning": .string("Trust this folder?"), "answerable": .bool(true)])))
    let trust = try question.answer(choice: .trust, currentQuestionID: question.id, machinePublicKey: machine, signerPrivateKey: commandPhoneKey,
                                    capabilities: ["launch.trust"], isLocal: false, requestID: commandRequest, now: commandNow)
    #expect(trust.data["command"]?["mac"]?.string == "OO_ZAWL1C15dpDTn1IcnT-RZXcsEtdN-9poKPLlx5bA")
    let quit = try question.answer(choice: .quit, currentQuestionID: question.id, machinePublicKey: machine, signerPrivateKey: commandPhoneKey,
                                   capabilities: ["launch.trust"], isLocal: false, requestID: commandRequest, now: commandNow)
    #expect(quit.data["command"]?["mac"] != trust.data["command"]?["mac"])
    #expect(throws: BridgeOperationError.self) { try question.answer(choice: .trust, currentQuestionID: "changed", machinePublicKey: machine, signerPrivateKey: commandPhoneKey, capabilities: ["launch.trust"], isLocal: false) }
    #expect(throws: BridgeOperationError.self) { try question.answer(choice: .trust, currentQuestionID: question.id, machinePublicKey: machine, signerPrivateKey: commandPhoneKey, capabilities: ["launch.trust"], isLocal: true) }
    #expect(BridgeTrustResult(json: .object(["outcome": .string("future")])).outcome == .unconfirmed)
}

@Test func signedCommandsRejectMismatchedKeysAndInvalidDestinations() throws {
    let machine = try P256.KeyAgreement.PrivateKey(rawRepresentation: commandMachineKey).publicKey.x963Representation
    #expect(throws: BridgeOperationError.self) { try BridgeSignedCommand.deriveKey(privateKey: commandPhoneKey, peerPublicKey: machine, signerID: String(repeating: "a", count: 32), machineID: BridgeSignedCommand.deviceID(machine)) }
    #expect(throws: BridgeOperationError.self) { try BridgeSignedCommand.sign(action: .transfer, targetMachinePublicKey: machine, targetSession: "s", to: "bad", signerPrivateKey: commandPhoneKey) }
    #expect(throws: BridgeOperationError.self) { try BridgeSignedCommand.sign(action: .trust, targetMachinePublicKey: machine, targetSession: "q", to: String(repeating: "b", count: 32), signerPrivateKey: commandPhoneKey) }
    #expect(BridgeBase64URL.decode("AB") == nil)
    #expect(BridgeBase64URL.decode("AA=") == nil)
}

@Test func transferredLaunchBindsTheSourceAndTargetWithoutRequiringANewPrompt() throws {
    let sourceKey = try P256.KeyAgreement.PrivateKey(rawRepresentation: commandMachineKey).publicKey.x963Representation
    let session = try #require(BridgeResumableSession(json: .object(["id": .string("source-session"), "agent": .string("codex"), "launcher": .string("codex")])))
    let options = BridgeLaunchOptions(json: .object(["agents": .array([.object(["id": .string("codex"), "kind": .string("codex"), "needsPrompt": .bool(true)])]), "workspaces": .array([.object(["label": .string("Bridge"), "path": .string("/workspace/bridge")])])]))
    var draft = BridgeLaunchDraft()
    draft.agent = "codex"
    draft.workspace = "Bridge"
    let operation = try BridgeTransfer.launch(draft: draft, sourceSession: session, sourcePublicKey: sourceKey, targetMachineID: "00112233445566778899aabbccddeeff", signerPrivateKey: commandPhoneKey,
                                              options: options, capabilities: ["sessions.launch", "sessions.transfer"], sourceCapabilities: ["sessions.transfer.serve"], isLocal: false, requestID: commandRequest, now: commandNow)
    #expect(operation.data["prompt"] == nil)
    let encodedBody = try #require(operation.data["transfer"]?["command"]?["body"]?.string)
    let bodyBytes = try #require(BridgeBase64URL.decode(encodedBody))
    let body = try JSONDecoder().decode(JSONValue.self, from: bodyBytes)
    #expect(body["target"]?["machine"]?.string == BridgeSignedCommand.deviceID(sourceKey))
    #expect(body["target"]?["session"]?.string == "source-session")
    #expect(body["to"]?.string == "00112233445566778899aabbccddeeff")
    #expect(body["request"] == operation.data["request"])
    draft.workspace = nil
    #expect(throws: BridgeOperationError.self) { try BridgeTransfer.launch(draft: draft, sourceSession: session, sourcePublicKey: sourceKey, targetMachineID: "00112233445566778899aabbccddeeff", signerPrivateKey: commandPhoneKey, options: options, capabilities: ["sessions.launch", "sessions.transfer"], sourceCapabilities: ["sessions.transfer.serve"], isLocal: false) }
}
