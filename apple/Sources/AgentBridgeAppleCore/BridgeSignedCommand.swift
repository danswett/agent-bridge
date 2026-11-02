import CryptoKit
import Foundation

public enum BridgeCommandAction: String, Sendable {
    case transfer = "session.transfer"
    case trust = "launch.trust:trust"
    case quit = "launch.trust:quit"
}

/// The body is sent as exact bytes, so another machine carrying a command cannot
/// rewrite its recipient, choice, question or request identity during a retry.
public struct BridgeSignedCommand: Sendable, Equatable {
    public let body: String
    public let by: String
    public let mac: String
    public let requestID: UUID
    public let expiresAt: Date
    public var json: JSONValue {
        .object(["body": .string(body), "by": .string(by), "mac": .string(mac)])
    }

    public static func sign(action: BridgeCommandAction, targetMachinePublicKey: Data, targetSession: String,
                            to: String, signerPrivateKey: Data, requestID: UUID = UUID(), now: Date = Date()) throws -> Self
    {
        let signer = try P256.KeyAgreement.PrivateKey(rawRepresentation: signerPrivateKey)
        let machine = try publicKey(targetMachinePublicKey)
        let signerID = deviceID(signer.publicKey.x963Representation)
        let machineID = deviceID(machine.x963Representation)
        guard validDeviceID(to), signerID != to, signerID != machineID else { throw BridgeOperationError("The command must be addressed to another machine.") }
        guard !targetSession.isEmpty, targetSession.utf8.count <= 256,
              !targetSession.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else { throw BridgeOperationError("The command's session or question is invalid.") }
        if action != .transfer, to != machineID {
            throw BridgeOperationError("A folder-trust answer must return to the machine asking it.")
        }
        let timestamp = now.timeIntervalSince1970.rounded(.down)
        guard timestamp.isFinite, timestamp >= 0, timestamp <= 253_402_300_499 else { throw BridgeOperationError("The command's time is invalid.") }
        let issued = Int64(timestamp)
        let expires = issued + 300
        // Field order matches the published C#/PWA vectors; the MAC always uses these
        // bytes directly, without a JSON decode/re-encode step on the receiving side.
        let json = try "{\"v\":1,\"action\":\(quote(action.rawValue)),\"request\":\(quote(requestID.uuidString.lowercased())),\"principal\":{\"kind\":\"user\",\"device\":\(quote(signerID))},\"target\":{\"machine\":\(quote(machineID)),\"session\":\(quote(targetSession))},\"to\":\(quote(to)),\"issuedAt\":\(issued),\"expiresAt\":\(expires)}"
        let bytes = Data(json.utf8)
        let body = BridgeBase64URL.encode(bytes)
        guard body.utf8.count <= 2048 else { throw BridgeOperationError("The signed command is too large.") }
        let key = try deriveKey(privateKey: signerPrivateKey, peerPublicKey: targetMachinePublicKey, signerID: signerID, machineID: machineID)
        let mac = HMAC<SHA256>.authenticationCode(for: bytes, using: key)
        return Self(body: body, by: BridgeBase64URL.encode(signer.publicKey.x963Representation), mac: BridgeBase64URL.encode(Data(mac)), requestID: requestID, expiresAt: Date(timeIntervalSince1970: Double(expires)))
    }

    public static func deriveKey(privateKey: Data, peerPublicKey: Data, signerID: String, machineID: String) throws -> SymmetricKey {
        guard validDeviceID(signerID), validDeviceID(machineID) else { throw BridgeOperationError("The command device identity is invalid.") }
        let key = try P256.KeyAgreement.PrivateKey(rawRepresentation: privateKey)
        let peer = try publicKey(peerPublicKey)
        let ownID = deviceID(key.publicKey.x963Representation)
        let peerID = deviceID(peerPublicKey)
        guard (ownID == signerID && peerID == machineID) || (ownID == machineID && peerID == signerID) else {
            throw BridgeOperationError("The command device identities do not match the keys.")
        }
        let shared = try key.sharedSecretFromKeyAgreement(with: peer)
        return shared.hkdfDerivedSymmetricKey(using: SHA256.self, salt: Data("agent-bridge command v1".utf8), sharedInfo: Data("\(signerID)|\(machineID)".utf8), outputByteCount: 32)
    }

    public static func deviceID(_ publicKey: Data) -> String {
        SHA256.hash(data: publicKey).prefix(16).map { String(format: "%02x", $0) }.joined()
    }

    static func validDeviceID(_ id: String) -> Bool {
        id.utf8.count == 32 && id.utf8.allSatisfy { (48 ... 57).contains($0) || (97 ... 102).contains($0) }
    }

    private static func publicKey(_ bytes: Data) throws -> P256.KeyAgreement.PublicKey {
        guard bytes.count == 65, bytes.first == 4 else { throw BridgeOperationError("A machine key must be an uncompressed P-256 point.") }
        return try P256.KeyAgreement.PublicKey(x963Representation: bytes)
    }

    private static func quote(_ text: String) throws -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.withoutEscapingSlashes]
        return try String(decoding: encoder.encode(text), as: UTF8.self)
    }
}

public struct BridgeTrustQuestion: Identifiable, Sendable, Equatable {
    public enum Choice: String, Sendable { case trust, quit }
    public let id: String
    public let agent: String
    public let label: String
    public let workspace: String?
    public let folder: String
    public let root: String?
    public let rootNote: String?
    public let warning: String?
    public let answerable: Bool
    public let atMachine: String?

    public init?(json: JSONValue) {
        guard let id = json["id"]?.string, !id.isEmpty, let folder = json["folder"]?.string, !folder.isEmpty else { return nil }
        self.id = id
        self.folder = folder
        agent = json["agent"]?.string ?? "unknown"
        label = json["label"]?.string ?? agent
        workspace = json["workspace"]?.string
        root = json["root"]?.string
        rootNote = json["rootNote"]?.string
        warning = json["warning"]?.string
        answerable = json["answerable"]?.bool == true
        atMachine = json["atMachine"]?.string
    }

    public func answer(choice: Choice, currentQuestionID: String?, machinePublicKey: Data, signerPrivateKey: Data,
                       capabilities: Set<String>, isLocal: Bool, requestID: UUID = UUID(), now: Date = Date()) throws -> BridgeOperation
    {
        try BridgeOperationValidation.require("launch.trust", in: capabilities)
        guard !isLocal, answerable, let warning, !warning.isEmpty, warning.utf8.count <= 2048 else {
            throw BridgeOperationError(atMachine ?? "Answer this folder-trust question at the machine.")
        }
        guard currentQuestionID == id else { throw BridgeOperationError("This folder-trust question is no longer pending.") }
        let command = try BridgeSignedCommand.sign(action: choice == .trust ? .trust : .quit, targetMachinePublicKey: machinePublicKey,
                                                   targetSession: id, to: BridgeSignedCommand.deviceID(machinePublicKey), signerPrivateKey: signerPrivateKey, requestID: requestID, now: now)
        return BridgeOperation(op: "launch.trust.answer", data: ["question": .string(id), "choice": .string(choice.rawValue), "command": command.json], requestID: requestID)
    }
}

public struct BridgeTrustResult: Sendable, Equatable {
    public enum Outcome: String, Sendable { case started, declined, unconfirmed, refused }
    public let outcome: Outcome
    public let reason: String?
    public let sessionID: String?

    public init(json: JSONValue) {
        outcome = Outcome(rawValue: json["outcome"]?.string ?? "") ?? .unconfirmed
        reason = json["reason"]?.string
        sessionID = json["session"]?.string
    }
}

enum BridgeBase64URL {
    static func encode(_ data: Data) -> String {
        data.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }

    static func decode(_ text: String) -> Data? {
        guard !text.isEmpty, text.utf8.allSatisfy({ (65 ... 90).contains($0) || (97 ... 122).contains($0) || (48 ... 57).contains($0) || $0 == 45 || $0 == 95 }) else { return nil }
        let padded = text.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/") + String(repeating: "=", count: (4 - text.count % 4) % 4)
        guard let bytes = Data(base64Encoded: padded), encode(bytes) == text else { return nil }
        return bytes
    }
}
