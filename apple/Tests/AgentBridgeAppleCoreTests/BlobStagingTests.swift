@testable import AgentBridgeAppleCore
import Foundation
import Testing

private actor BlobStagingPeer {
    private var states: [JSONValue]
    private(set) var requests: [BridgeOperation] = []
    private(set) var timeouts: [Duration] = []
    private(set) var deleted = false
    private var connected = true
    private let disconnectAfterResponse: Bool
    private let failure: Bool
    private var firstRequest: CheckedContinuation<Void, Never>?

    init(_ states: [(Int, Bool)], disconnectAfterResponse: Bool = false, failure: Bool = false) {
        self.states = states.map { .object(["received": .number(Double($0.0)), "complete": .bool($0.1)]) }
        self.disconnectAfterResponse = disconnectAfterResponse
        self.failure = failure
    }

    func validate() throws {
        guard connected else { throw RelayError.disconnected }
    }

    func send(_ operation: BridgeOperation, timeout: Duration) throws -> JSONValue {
        try Task.checkCancellation()
        try validate()
        requests.append(operation)
        timeouts.append(timeout)
        firstRequest?.resume()
        firstRequest = nil
        if operation.op == "attachment.discard" {
            return .object([:])
        }
        if failure {
            throw RelayError.refused("The blob digest did not match.")
        }
        if disconnectAfterResponse {
            connected = false
        }
        return states.count > 1 ? states.removeFirst() : states[0]
    }

    func delete() throws {
        try Task.checkCancellation()
        deleted = true
    }

    func waitForStart() async {
        guard requests.isEmpty else { return }
        await withCheckedContinuation { firstRequest = $0 }
    }

    func confirm(_ attachment: BridgeBlobAttachment, timeout: Duration = .seconds(5), pollInterval: Duration = .milliseconds(1)) async throws -> UUID {
        try await BridgeBlobStaging.confirm(attachment, ticket: "AAAAAAAAAAAAAAAAAAAAAA.AA.AA", key: Data(repeating: 7, count: 32),
                                            capabilities: ["sessions.reply", "sessions.attach", "sessions.attach.blob"], timeout: timeout, pollInterval: pollInterval,
                                            validateConnection: { try await self.validate() }, send: { try await self.send($0, timeout: $1) },
                                            deleteBlob: { try await self.delete() })
    }
}

private func stagingAttachment() throws -> BridgeBlobAttachment {
    try BridgeBlobAttachment(name: "fixture.bin", size: 300_000, sha256: Data(repeating: 3, count: 32))
}

@Test func blobStagingPollsUnchangedMetadataUntilTheHostFinishesVerification() async throws {
    let attachment = try stagingAttachment()
    let peer = BlobStagingPeer([(0, false), (120_000, false), (attachment.size, false), (attachment.size, true)])
    #expect(try await peer.confirm(attachment) == attachment.id)
    let requests = await peer.requests
    #expect(requests.count == 4)
    #expect(requests.allSatisfy { $0 == requests[0] && $0.op == "attachment.start" })
    #expect(requests[0].data["attachment"] == .string(attachment.id.uuidString.lowercased()))
    #expect(await !peer.deleted)
}

@Test func blobStagingDeadlineBoundsEveryPollAndCleansUpTheSameAttachment() async throws {
    let attachment = try stagingAttachment()
    let peer = BlobStagingPeer([(0, false)])
    await #expect(throws: RelayError.timeout) {
        try await peer.confirm(attachment, timeout: .milliseconds(200), pollInterval: .seconds(1))
    }
    let requests = await peer.requests
    #expect(requests.map(\.op) == ["attachment.start", "attachment.discard"])
    #expect(requests.last?.data["attachment"] == .string(attachment.id.uuidString.lowercased()))
    #expect(try await #require(peer.timeouts.first) <= .milliseconds(200))
    #expect(await peer.deleted)
}

@Test func cancellingBlobStagingStillDiscardsAndDeletesFromANoncancelledTask() async throws {
    let attachment = try stagingAttachment()
    let peer = BlobStagingPeer([(0, false)])
    let task = Task { try await peer.confirm(attachment, pollInterval: .seconds(1)) }
    await peer.waitForStart()
    task.cancel()
    await #expect(throws: CancellationError.self) { try await task.value }
    #expect(await peer.requests.map(\.op) == ["attachment.start", "attachment.discard"])
    #expect(await peer.deleted)
}

@Test func aChangedConnectionCannotConfirmOrDiscardOnItsReplacement() async throws {
    let attachment = try stagingAttachment()
    let peer = BlobStagingPeer([(attachment.size, true)], disconnectAfterResponse: true)
    await #expect(throws: RelayError.disconnected) { try await peer.confirm(attachment) }
    #expect(await peer.requests.map(\.op) == ["attachment.start"])
    #expect(await peer.deleted)
}

@Test func invalidBlobProgressAndHostFetchFailuresAreNotRetried() async throws {
    let attachment = try stagingAttachment()
    for states in [[(1, true)], [(attachment.size + 1, false)], [(100, false), (99, false)]] {
        let peer = BlobStagingPeer(states)
        await #expect(throws: BridgeOperationError.self) { try await peer.confirm(attachment) }
        #expect(await peer.requests.last?.op == "attachment.discard")
        #expect(await peer.deleted)
    }
    let peer = BlobStagingPeer([(0, false)], failure: true)
    await #expect(throws: RelayError.refused("The blob digest did not match.")) { try await peer.confirm(attachment) }
    #expect(await peer.requests.map(\.op) == ["attachment.start", "attachment.discard"])
    #expect(await peer.deleted)
}
