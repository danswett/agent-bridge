@testable import AgentBridgeAppleCore
import CryptoKit
import Foundation
import Testing

private enum BlobStreamFailure: CaseIterable, Sendable {
    case truncatedRecord, corruptTag, nonzeroPadding, wrongHash, trailingByte
}

@Test(arguments: BlobStreamFailure.allCases)
private func streamingBlobRejectsIncompleteOrUnverifiedFiles(_ failure: BlobStreamFailure) throws {
    let key = Data(0 ..< 32)
    let id = Data(0xA0 ..< 0xB0)
    let plaintext = Data((0 ..< 70000).map { UInt8($0 % 251) })
    var stored = try BridgeBlob.seal(plaintext, key: key, id: id)
    var digest = Data(SHA256.hash(data: plaintext))
    switch failure {
    case .truncatedRecord: stored = Data(stored.prefix(BridgeBlob.recordBytes + 12))
    case .corruptTag: stored[BridgeBlob.recordBytes + 5] ^= 1
    case .nonzeroPadding: stored[stored.count - 1] = 1
    case .wrongHash: digest[0] ^= 1
    case .trailingByte: stored.append(0)
    }

    let directory = FileManager.default.temporaryDirectory.appendingPathComponent("agent-bridge-invalid-stream-\(UUID().uuidString)", isDirectory: true)
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false)
    defer { try? FileManager.default.removeItem(at: directory) }
    let storedURL = directory.appendingPathComponent("stored")
    let provisionalURL = directory.appendingPathComponent("provisional")
    try stored.write(to: storedURL)
    try Data().write(to: provisionalURL)
    let input = try FileHandle(forReadingFrom: storedURL)
    let output = try FileHandle(forWritingTo: provisionalURL)
    defer { try? input.close(); try? output.close() }
    #expect(throws: (any Error).self) {
        try BridgeBlob.open(input: input, plaintextSize: plaintext.count, output: output,
                            key: key, id: id, expectedSHA256: digest)
    }
}
