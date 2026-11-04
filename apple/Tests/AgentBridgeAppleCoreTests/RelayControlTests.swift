@testable import AgentBridgeAppleCore
import Foundation
import Testing

private actor ControlRelay: RelaySocket {
    enum Behavior: Sendable { case automatic, reversePair, silent, wrongType, refused }
    var behavior: Behavior
    let blobMaximum: Int?
    private var incoming: [RelaySocketMessage] = []
    private var waiter: CheckedContinuation<RelaySocketMessage, any Error>?
    private var client = ""
    private(set) var controls: [JSONValue] = []
    private(set) var closed = false
    private let ephemeral = NoiseKeyPair()
    private var clientPublicKey = Data()
    let account = RelayAccount(token: "fixture-token", accountId: "fixture-account", login: "fixture")

    init(behavior: Behavior = .automatic, blobMaximum: Int? = 1_048_576) {
        self.behavior = behavior
        self.blobMaximum = blobMaximum
    }

    func send(_ message: RelaySocketMessage) throws {
        guard case let .text(text) = message else { throw RelayError.invalidMessage }
        let json = try JSONDecoder().decode(JSONValue.self, from: Data(text.utf8))
        switch json["t"]?.string {
        case "hello":
            clientPublicKey = try RelayWire.unhex(json["pub"]!.string!)
            client = try RelayWire.deviceID(publicKey: clientPublicKey)
            try push(["t": .string("challenge"), "eph": .string(RelayWire.hex(ephemeral.publicKey)),
                      "nonce": .string(RelayWire.hex(Data(repeating: 0x54, count: 32))), "account": .string(account.accountId)])
        case "proof":
            let mac = try RelayWire.proofMAC(sharedSecret: ephemeral.sharedSecret(with: clientPublicKey), nonce: Data(repeating: 0x54, count: 32), accountID: account.accountId)
            #expect(json["mac"] == .string(RelayWire.hex(mac)))
            var ready: [String: JSONValue] = ["t": .string("ready"), "device": .string(client)]
            ready["blobs"] = blobMaximum.map { .object(["max": .number(Double($0))]) } ?? .null
            try push(ready)
        case "blob-put", "blob-delete", "forget":
            controls.append(json)
            switch behavior {
            case .automatic: try respond(json)
            case .reversePair:
                if controls.count == 2 {
                    for control in controls.reversed() {
                        try respond(control)
                    }
                }
            case .silent: break
            case .wrongType: try respond(json, wrongType: true)
            case .refused: try respond(json, refused: true)
            }
        default: throw RelayError.invalidMessage
        }
    }

    func receive() async throws -> RelaySocketMessage {
        if !incoming.isEmpty {
            return incoming.removeFirst()
        }
        if closed {
            throw RelayError.disconnected
        }
        return try await withCheckedThrowingContinuation { waiter = $0 }
    }

    func ping() {}
    func close() {
        closed = true; waiter?.resume(throwing: RelayError.disconnected); waiter = nil
    }

    func setBehavior(_ behavior: Behavior) {
        self.behavior = behavior
    }

    func respondLate() throws {
        if let first = controls.first {
            try respond(first)
        }
    }

    private func respond(_ control: JSONValue, wrongType: Bool = false, refused: Bool = false) throws {
        let type = control["t"] == .string("forget") ? "forget-result" : "blob-result"
        var value: [String: JSONValue] = ["t": .string(wrongType ? (type == "blob-result" ? "forget-result" : "blob-result") : type), "id": control["id"]!]
        if refused {
            value["error"] = .string("quota")
        }
        if control["t"] == .string("blob-put") {
            value["blob"] = .string("fixture-ticket")
            value["url"] = .string("https://relay.example/blob")
        }
        try push(value)
    }

    private func push(_ value: [String: JSONValue]) throws {
        let message = try RelaySocketMessage.text(String(decoding: JSONEncoder().encode(JSONValue.object(value)), as: UTF8.self))
        if let waiter {
            self.waiter = nil; waiter.resume(returning: message)
        } else {
            incoming.append(message)
        }
    }
}

private func controlFixture(_ socket: ControlRelay) -> RelayBridge {
    RelayBridge(socket: socket, account: socket.account, identity: NoiseKeyPair())
}

@Test func mixedRelayControlsCorrelateOutOfOrderResultsWithoutSharingIDs() async throws {
    let socket = ControlRelay(behavior: .reversePair)
    let relay = controlFixture(socket)
    let events = try await relay.connect()
    let peer = String(repeating: "a", count: 32)
    async let blob = relay.blobPut(recipient: peer, plaintextSize: 300_000)
    async let forget: Void = relay.forget(machineID: peer)
    let (result, _) = try await (blob, forget)
    #expect(result["blob"] == .string("fixture-ticket"))
    let controls = await socket.controls
    #expect(controls.count == 2)
    #expect(Set(controls.compactMap { $0["id"]?.number }).count == 2)
    #expect(controls.first { $0["t"] == .string("blob-put") }?["size"] == .number(524_288))
    withExtendedLifetime(events) {}
    await relay.disconnect()
}

@Test func aRelayWithoutBlobStorageNeverReceivesAnAllocationRequest() async throws {
    let socket = ControlRelay(blobMaximum: nil)
    let relay = controlFixture(socket)
    let events = try await relay.connect()
    await #expect(throws: (any Error).self) { try await relay.blobPut(recipient: String(repeating: "a", count: 32), plaintextSize: 300_000) }
    #expect(await socket.controls.isEmpty)
    #expect(await relay.blobMaximum == 0)
    withExtendedLifetime(events) {}
    await relay.disconnect()
}

@Test func mismatchedRelayControlResponseTypeDisconnectsInsteadOfCompletingTheWrongOperation() async throws {
    let socket = ControlRelay(behavior: .wrongType)
    let relay = controlFixture(socket)
    let events = try await relay.connect()
    await #expect(throws: RelayError.invalidMessage) { try await relay.forget(machineID: String(repeating: "a", count: 32)) }
    await #expect(throws: RelayError.disconnected) { try await relay.forget(machineID: String(repeating: "a", count: 32)) }
    withExtendedLifetime(events) {}
    await relay.disconnect()
}

@Test func cancellingARelayControlDropsItsLateResponseWithoutReplayingOrDisconnecting() async throws {
    let socket = ControlRelay(behavior: .silent)
    let relay = controlFixture(socket)
    let events = try await relay.connect()
    let request = Task { try await relay.forget(machineID: String(repeating: "a", count: 32)) }
    for _ in 0 ..< 200 {
        if await socket.controls.count == 1 {
            break
        }
        try await Task.sleep(for: .milliseconds(5))
    }
    #expect(await socket.controls.count == 1)
    request.cancel()
    await #expect(throws: CancellationError.self) { try await request.value }
    try await socket.respondLate()
    await socket.setBehavior(.automatic)
    try await relay.forget(machineID: String(repeating: "b", count: 32))
    #expect(await socket.controls.count == 2)
    #expect(await socket.closed == false)
    withExtendedLifetime(events) {}
    await relay.disconnect()
}

@Test func aRefusedRelayControlFailsOnlyItsOwnRequest() async throws {
    let socket = ControlRelay(behavior: .refused)
    let relay = controlFixture(socket)
    let events = try await relay.connect()
    await #expect(throws: RelayError.refused("The relay refused this request.")) {
        try await relay.blobPut(recipient: String(repeating: "a", count: 32), plaintextSize: 300_000)
    }
    await socket.setBehavior(.automatic)
    try await relay.forget(machineID: String(repeating: "a", count: 32))
    #expect(await socket.closed == false)
    withExtendedLifetime(events) {}
    await relay.disconnect()
}

@Test func failedBlobUploadCleanupStillDeletesTheTicketWhenItsCallerIsCancelled() async throws {
    let socket = ControlRelay()
    let relay = controlFixture(socket)
    let events = try await relay.connect()
    let cleanup = Task {
        withUnsafeCurrentTask { $0?.cancel() }
        await RelayBlobUpload.cleanup(ticket: "AAAAAAAAAAAAAAAAAAAAAA.AA.AA", relay: relay)
    }
    await cleanup.value
    #expect(await socket.controls.map { $0["t"]?.string } == ["blob-delete"])
    #expect(await socket.closed == false)
    withExtendedLifetime(events) {}
    await relay.disconnect()
}

@Test func blobDeletionTimeoutDoesNotDisconnectOrReplayOtherRelayControls() async throws {
    let socket = ControlRelay(behavior: .silent)
    let relay = controlFixture(socket)
    let events = try await relay.connect()
    await #expect(throws: RelayError.timeout) {
        try await relay.blobDelete(ticket: "AAAAAAAAAAAAAAAAAAAAAA.AA.AA", timeout: .milliseconds(100))
    }
    await socket.setBehavior(.automatic)
    try await relay.forget(machineID: String(repeating: "a", count: 32))
    #expect(await socket.controls.map { $0["t"]?.string } == ["blob-delete", "forget"])
    #expect(await socket.closed == false)
    withExtendedLifetime(events) {}
    await relay.disconnect()
}
