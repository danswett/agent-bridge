import AgentBridgeAppleCore
@testable import AgentBridgeClientState
import CoreGraphics
import Foundation
import ImageIO
import Testing
import UniformTypeIdentifiers

private actor UploadFixture {
    var bytes = Data()
    var operations: [String] = []
    var size = 0
    var corruptAcknowledgment = false
    var disconnectAfterChunk = false
    var blobCalls = 0
    var honorCancellation = false

    func configure(corrupt: Bool = false, disconnect: Bool = false, honorCancellation: Bool = false) {
        corruptAcknowledgment = corrupt; disconnectAfterChunk = disconnect
        self.honorCancellation = honorCancellation
    }

    func validate() throws {
        if disconnectAfterChunk, operations.contains("attachment.chunk") {
            throw BridgeOperationError("Connection changed")
        }
    }

    func send(_ operation: BridgeOperation) throws -> JSONValue {
        if honorCancellation {
            try Task.checkCancellation()
        }
        operations.append(operation.op)
        if operation.op == "attachment.start" {
            size = Int(operation.data["size"]?.number ?? 0)
            return .object(["received": .number(0), "complete": .bool(false)])
        }
        if operation.op == "attachment.discard" {
            return .object(["discarded": .bool(true)])
        }
        let chunk = try #require(Data(base64Encoded: operation.data["data"]?.string ?? ""))
        #expect(chunk.count <= BridgeAttachment.chunkBytes)
        #expect(try JSONEncoder().encode(operation.request(id: 1)).count <= 16 * 1024)
        #expect(operation.data["offset"]?.number == Double(bytes.count))
        bytes.append(chunk)
        return .object(["received": .number(Double(bytes.count + (corruptAcknowledgment ? 1 : 0))), "complete": .bool(bytes.count == size)])
    }

    func blob(_ url: URL) throws -> UUID {
        #expect(try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0 > BridgeAttachment.maximumBytes)
        blobCalls += 1
        return UUID()
    }

    func endpoint(local: Bool = true) -> BridgeAttachmentUploadEndpoint {
        BridgeAttachmentUploadEndpoint(capabilities: ["sessions.reply", "sessions.attach", local ? "sessions.attach.direct" : "sessions.attach.blob"], isLocal: local,
                                       validateConnection: { try await self.validate() }, send: { try await self.send($0) }, uploadBlob: { url, _ in try await self.blob(url) })
    }
}

private actor UploadProgressFixture {
    var updates: [BridgeAttachmentUploadProgress] = []
    func append(_ progress: BridgeAttachmentUploadProgress) {
        updates.append(progress)
    }
}

@Test func fastAttachmentAcknowledgmentsDoNotFloodTheUIWithProgressUpdates() async throws {
    let file = try uploadFile(Data(repeating: 42, count: 300_000))
    defer { try? FileManager.default.removeItem(at: file.deletingLastPathComponent()) }
    let fixture = UploadFixture()
    let progress = UploadProgressFixture()
    let instant = ContinuousClock.now
    let uploader = BridgeAttachmentUploader(progressClock: { instant })
    _ = try await uploader.upload(file: file, existingCount: 0, endpoint: fixture.endpoint()) { await progress.append($0) }
    #expect(await fixture.operations.filter { $0 == "attachment.chunk" }.count > 20)
    let updates = await progress.updates
    #expect(updates.count == 3) // Preparation, first progress, and final acknowledgment.
    #expect(updates.first?.fraction == nil)
    #expect(updates.last?.fraction == 1)
}

@Test func cancellingAtTheFinalChunkDiscardsTheUploadedFileInsteadOfReportingSuccess() async throws {
    let file = try uploadFile(Data(repeating: 42, count: 100))
    defer { try? FileManager.default.removeItem(at: file.deletingLastPathComponent()) }
    let fixture = UploadFixture()
    await fixture.configure(honorCancellation: true)
    let task = Task {
        try await BridgeAttachmentUploader().upload(file: file, existingCount: 0, endpoint: fixture.endpoint()) { progress in
            if progress.fraction == 1 {
                withUnsafeCurrentTask { $0?.cancel() }
            }
        }
    }
    await #expect(throws: CancellationError.self) { try await task.value }
    #expect(await fixture.operations == ["attachment.start", "attachment.chunk", "attachment.discard"])
}

@Test(arguments: [false, true]) func cancelledBlobHandoffsStillDiscardOnTheOriginalConnection(cancelOnProgress: Bool) async throws {
    let file = try uploadFile(Data(repeating: 42, count: 300_000))
    defer { try? FileManager.default.removeItem(at: file.deletingLastPathComponent()) }
    let fixture = UploadFixture()
    await fixture.configure(honorCancellation: true)
    let endpoint = BridgeAttachmentUploadEndpoint(capabilities: ["sessions.reply", "sessions.attach", "sessions.attach.blob"], isLocal: false,
                                                  validateConnection: { try await fixture.validate() }, send: { try await fixture.send($0) }, uploadBlob: { url, _ in
                                                      let id = try await fixture.blob(url)
                                                      if !cancelOnProgress {
                                                          withUnsafeCurrentTask { $0?.cancel() }
                                                      }
                                                      return id
                                                  })
    let task = Task {
        try await BridgeAttachmentUploader().upload(file: file, existingCount: 0, endpoint: endpoint) { progress in
            if cancelOnProgress, progress.fraction == 1 {
                withUnsafeCurrentTask { $0?.cancel() }
            }
        }
    }
    await #expect(throws: CancellationError.self) { try await task.value }
    #expect(await fixture.operations == ["attachment.discard"])
}

private func uploadFile(_ bytes: Data, extension ext: String = "bin") throws -> URL {
    let directory = try BridgeAttachmentImage.temporaryDirectory()
    let file = directory.appendingPathComponent("attachment." + ext)
    try bytes.write(to: file)
    return file
}

@Test func attachmentUploaderStreamsBoundedChunksAndReconstructsTheFile() async throws {
    let bytes = Data(repeating: 255, count: 500_000)
    let file = try uploadFile(bytes)
    defer { try? FileManager.default.removeItem(at: file.deletingLastPathComponent()) }
    let fixture = UploadFixture()
    let uploaded = try await BridgeAttachmentUploader().upload(file: file, existingCount: 0, endpoint: fixture.endpoint())
    #expect(uploaded.name == file.lastPathComponent)
    #expect(!uploaded.resized)
    #expect(await fixture.bytes == bytes)
    #expect(await fixture.operations.filter { $0 == "attachment.start" }.count == 1)
}

@Test func attachmentUploaderRejectsBadAcknowledgmentsWithoutResending() async throws {
    let file = try uploadFile(Data(repeating: 42, count: 20000))
    defer { try? FileManager.default.removeItem(at: file.deletingLastPathComponent()) }
    let fixture = UploadFixture()
    await fixture.configure(corrupt: true)
    await #expect(throws: BridgeOperationError.self) { try await BridgeAttachmentUploader().upload(file: file, existingCount: 0, endpoint: fixture.endpoint()) }
    #expect(await fixture.operations == ["attachment.start", "attachment.chunk", "attachment.discard"])
}

@Test func attachmentUploaderStopsWhenThePinnedConnectionChanges() async throws {
    let file = try uploadFile(Data(repeating: 42, count: 20000))
    defer { try? FileManager.default.removeItem(at: file.deletingLastPathComponent()) }
    let fixture = UploadFixture()
    await fixture.configure(disconnect: true)
    await #expect(throws: BridgeOperationError.self) { try await BridgeAttachmentUploader().upload(file: file, existingCount: 0, endpoint: fixture.endpoint()) }
    #expect(await fixture.operations == ["attachment.start", "attachment.chunk"])
}

@Test func largeRemoteFilesUseTheBlobPathAndTenAttachmentsIsTheLimit() async throws {
    let file = try uploadFile(Data(repeating: 42, count: 300_000))
    defer { try? FileManager.default.removeItem(at: file.deletingLastPathComponent()) }
    let fixture = UploadFixture()
    let uploader = BridgeAttachmentUploader()
    _ = try await uploader.upload(file: file, existingCount: 9, endpoint: fixture.endpoint(local: false))
    #expect(await fixture.blobCalls == 1)
    #expect(await fixture.operations.isEmpty)
    await #expect(throws: BridgeOperationError.self) { try await uploader.upload(file: file, existingCount: 10, endpoint: fixture.endpoint(local: false)) }
    #expect(await fixture.blobCalls == 1)
}

@Test func oversizedImagesAreDownsampledToAFileThatFitsWithoutChangingTheOriginal() throws {
    let file = try uploadFile(Data(), extension: "png")
    defer { try? FileManager.default.removeItem(at: file.deletingLastPathComponent()) }
    var state: UInt32 = 42
    var pixels = [UInt8](repeating: 255, count: 512 * 512 * 4)
    for index in pixels.indices where index % 4 != 3 {
        state = state &* 1_664_525 &+ 1_013_904_223
        pixels[index] = UInt8(truncatingIfNeeded: state >> 24)
    }
    try pixels.withUnsafeMutableBytes { buffer in
        let context = try #require(CGContext(data: buffer.baseAddress, width: 512, height: 512, bitsPerComponent: 8, bytesPerRow: 512 * 4,
                                             space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue))
        let image = try #require(context.makeImage())
        let destination = try #require(CGImageDestinationCreateWithURL(file as CFURL, UTType.png.identifier as CFString, 1, nil))
        CGImageDestinationAddImage(destination, image, nil)
        #expect(CGImageDestinationFinalize(destination))
    }
    let original = try Data(contentsOf: file)
    #expect(original.count > 32 * 1024)
    let prepared = try BridgeAttachmentImage.prepare(file, maximumBytes: 32 * 1024)
    defer { prepared.removeTemporaryFile() }
    #expect(prepared.resized)
    #expect(prepared.type == "image/jpeg")
    #expect(prepared.size <= 32 * 1024)
    #expect(prepared.url != file)
    #expect(try Data(contentsOf: file) == original)
}

@Test func oversizedNonimagesAreRefusedBeforeAnyUploadStarts() async throws {
    let file = try uploadFile(Data(repeating: 42, count: 300_000))
    defer { try? FileManager.default.removeItem(at: file.deletingLastPathComponent()) }
    let fixture = UploadFixture()
    let endpoint = BridgeAttachmentUploadEndpoint(capabilities: ["sessions.reply", "sessions.attach"], isLocal: false,
                                                  validateConnection: {}, send: { try await fixture.send($0) }, uploadBlob: { _, _ in UUID() })
    await #expect(throws: BridgeOperationError.self) { try await BridgeAttachmentUploader().upload(file: file, existingCount: 0, endpoint: endpoint) }
    #expect(await fixture.operations.isEmpty)
}

@Test func clipboardTIFFImagesAreConvertedToAnAgentCompatibleJPEG() throws {
    let file = try uploadFile(Data(), extension: "tiff")
    defer { try? FileManager.default.removeItem(at: file.deletingLastPathComponent()) }
    let context = try #require(CGContext(data: nil, width: 32, height: 32, bitsPerComponent: 8, bytesPerRow: 128,
                                         space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue))
    context.setFillColor(CGColor(gray: 1, alpha: 1))
    context.fill(CGRect(x: 0, y: 0, width: 32, height: 32))
    let image = try #require(context.makeImage())
    let destination = try #require(CGImageDestinationCreateWithURL(file as CFURL, UTType.tiff.identifier as CFString, 1, nil))
    CGImageDestinationAddImage(destination, image, nil)
    #expect(CGImageDestinationFinalize(destination))
    let prepared = try BridgeAttachmentImage.prepare(file, maximumBytes: BridgeAttachment.maximumBytes)
    defer { prepared.removeTemporaryFile() }
    #expect(prepared.type == "image/jpeg")
    #expect(prepared.name.hasSuffix(".jpg"))
    #expect(prepared.resized)
}

@Test func filesOverTheInputLimitAreRefusedWithoutReadingTheirContents() async throws {
    let file = try uploadFile(Data())
    defer { try? FileManager.default.removeItem(at: file.deletingLastPathComponent()) }
    let handle = try FileHandle(forWritingTo: file)
    try handle.truncate(atOffset: UInt64(BridgeAttachmentImage.maximumInputBytes + 1))
    try handle.close()
    let fixture = UploadFixture()
    await #expect(throws: BridgeOperationError.self) { try await BridgeAttachmentUploader().upload(file: file, existingCount: 0, endpoint: fixture.endpoint(local: false)) }
    #expect(await fixture.blobCalls == 0)
    #expect(await fixture.operations.isEmpty)
}
