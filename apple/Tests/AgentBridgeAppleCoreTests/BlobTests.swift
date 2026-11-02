@testable import AgentBridgeAppleCore
import CryptoKit
import Foundation
import Testing

private let blobKey = Data(0 ..< 32)
private let blobID = Data(0xA0 ..< 0xB0)
private let blobPlaintext = Data((0 ..< 70000).map { UInt8($0 % 251) })

@Test func blobEncryptionMatchesTheHostAndPWAStoredVector() throws {
    let sealed = try BridgeBlob.seal(blobPlaintext, key: blobKey, id: blobID)
    #expect(sealed.count == 131_072)
    #expect(SHA256.hash(data: sealed).map { String(format: "%02x", $0) }.joined() == "3e6ef14bdfc54c85c017cf13d7013ecb53c98890cc29d2e1fa247217db3a3a8c")
    #expect(try BridgeBlob.open(sealed, plaintextSize: blobPlaintext.count, key: blobKey, id: blobID, expectedSHA256: Data(SHA256.hash(data: blobPlaintext))) == blobPlaintext)
}

@Test func blobRejectsTamperingTruncationWrongIdentityAndBadPadding() throws {
    let sealed = try BridgeBlob.seal(blobPlaintext, key: blobKey, id: blobID)
    let hash = Data(SHA256.hash(data: blobPlaintext))
    var changed = sealed
    changed[70000] ^= 1
    var padding = sealed
    padding[padding.count - 1] = 1
    var swapped = sealed
    let recordSize = BridgeBlob.recordBytes + 16
    swapped.replaceSubrange(0 ..< recordSize, with: sealed.suffix(recordSize))
    for invalid in [changed, padding, swapped, Data(sealed.dropLast()), sealed + Data([0])] {
        #expect(throws: (any Error).self) { try BridgeBlob.open(invalid, plaintextSize: blobPlaintext.count, key: blobKey, id: blobID, expectedSHA256: hash) }
    }
    #expect(throws: (any Error).self) { try BridgeBlob.open(sealed, plaintextSize: blobPlaintext.count, key: blobKey, id: Data(repeating: 0, count: 16), expectedSHA256: hash) }
    #expect(throws: (any Error).self) { try BridgeBlob.open(sealed, plaintextSize: blobPlaintext.count, key: Data(repeating: 0, count: 32), id: blobID, expectedSHA256: hash) }
    #expect(throws: (any Error).self) { try BridgeBlob.open(sealed, plaintextSize: 65536, key: blobKey, id: blobID, expectedSHA256: hash) }
    #expect(throws: BridgeOperationError.self) { try BridgeBlob.open(sealed, plaintextSize: blobPlaintext.count, key: blobKey, id: blobID, expectedSHA256: Data(repeating: 0, count: 32)) }
}

@Test func blobBucketsAndTicketsAreStrictlyBounded() throws {
    #expect(try BridgeBlob.storedSize(1) == 4096)
    #expect(try BridgeBlob.storedSize(4000) == 4096)
    #expect(try BridgeBlob.storedSize(5000) == 8192)
    #expect(try BridgeBlob.storedSize(1024 * 1024) == 2 * 1024 * 1024)
    let maximum = try BridgeBlob.maximumPlaintext(storedLimit: 1024 * 1024)
    #expect(try BridgeBlob.storedSize(maximum) == 1024 * 1024)
    #expect(try BridgeBlob.storedSize(maximum + 1) > 1024 * 1024)
    #expect(throws: BridgeOperationError.self) { try BridgeBlob.storedSize(Int.max) }
    #expect(throws: BridgeOperationError.self) { try BridgeBlob.storedSize(0) }
    #expect(try BridgeBlob.ticketID(BridgeBase64URL.encode(blobID) + ".Ym9keQ.bWFj") == blobID)
    #expect(throws: BridgeOperationError.self) { try BridgeBlob.ticketID(BridgeBase64URL.encode(blobID) + ".missing") }
}

@Test func blobURLsStayOnTheRelayOrDefaultHTTPSAzureOrigin() throws {
    let relay = try #require(URL(string: "https://relay.example"))
    #expect(try BridgeBlob.resolveURL("/v1/dev-blobs/x?sig=1", relay: relay).absoluteString == "https://relay.example/v1/dev-blobs/x?sig=1")
    #expect(try BridgeBlob.resolveURL("https://account.blob.core.windows.net/blobs/x?sig=1", relay: relay).host == "account.blob.core.windows.net")
    for invalid in ["http://account.blob.core.windows.net/blobs/x", "https://account.blob.core.windows.net:8443/x", "https://evil.example/blob.core.windows.net", "https://169.254.169.254/metadata", "https://user@account.blob.core.windows.net/x", "https://user@relay.example/x", "https://account.blob.core.windows.net.evil.example/x", "file:///tmp/x"] {
        #expect(throws: BridgeOperationError.self) { try BridgeBlob.resolveURL(invalid, relay: relay) }
    }
}

@Test func blobAttachmentMetadataAndRelayMessagesKeepTheKeyInsideTheChannel() throws {
    let size = 256 * 1024 + 1
    let put = try BridgeBlob.putMessage(id: 7, recipient: String(repeating: "a", count: 32), plaintextSize: size, storedLimit: 1024 * 1024)
    #expect(try put["size"]?.number == Double(BridgeBlob.storedSize(size)))
    #expect(put["key"] == nil)
    #expect(throws: BridgeOperationError.self) { try BridgeBlob.putMessage(id: 1, recipient: String(repeating: "a", count: 32), plaintextSize: size, storedLimit: 4096) }
    let attachment = try BridgeBlobAttachment(name: "report.pdf", size: size, sha256: Data(repeating: 0, count: 32))
    let ticket = BridgeBase64URL.encode(blobID) + ".Ym9keQ.bWFj"
    let operation = try attachment.start(ticket: ticket, key: blobKey, capabilities: ["sessions.reply", "sessions.attach", "sessions.attach.blob"], isLocal: false)
    #expect(operation.op == "attachment.start")
    #expect(operation.data["blob"]?["key"]?.string == BridgeBase64URL.encode(blobKey))
    #expect(throws: BridgeOperationError.self) { try attachment.start(ticket: ticket, key: blobKey, capabilities: ["sessions.reply", "sessions.attach", "sessions.attach.blob"], isLocal: true) }
    #expect(throws: BridgeOperationError.self) { try BridgeBlobAttachment(name: "too-large", size: 100 * 1024 * 1024 + 1, sha256: Data(repeating: 0, count: 32)) }
}

@Test func blobStreamingMatchesTheVectorAndChecksTheWholeFile() throws {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent("agent-bridge-blob-\(UUID().uuidString)", isDirectory: true)
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false)
    defer { try? FileManager.default.removeItem(at: directory) }
    let plainURL = directory.appendingPathComponent("plain")
    let sealedURL = directory.appendingPathComponent("sealed")
    let openedURL = directory.appendingPathComponent("opened")
    try blobPlaintext.write(to: plainURL)
    try Data().write(to: sealedURL)
    try Data().write(to: openedURL)
    let input = try FileHandle(forReadingFrom: plainURL)
    let sealedOutput = try FileHandle(forWritingTo: sealedURL)
    defer { try? input.close(); try? sealedOutput.close() }
    try BridgeBlob.seal(input: input, plaintextSize: blobPlaintext.count, output: sealedOutput, key: blobKey, id: blobID)
    #expect(try Data(contentsOf: sealedURL) == BridgeBlob.seal(blobPlaintext, key: blobKey, id: blobID))
    let sealedInput = try FileHandle(forReadingFrom: sealedURL)
    let openedOutput = try FileHandle(forWritingTo: openedURL)
    defer { try? sealedInput.close(); try? openedOutput.close() }
    try BridgeBlob.open(input: sealedInput, plaintextSize: blobPlaintext.count, output: openedOutput, key: blobKey, id: blobID, expectedSHA256: Data(SHA256.hash(data: blobPlaintext)))
    #expect(try Data(contentsOf: openedURL) == blobPlaintext)
    try input.seek(toOffset: 0)
    #expect(throws: BridgeOperationError.self) { try BridgeBlob.seal(input: input, plaintextSize: blobPlaintext.count - 1, output: sealedOutput, key: blobKey, id: blobID) }
}
