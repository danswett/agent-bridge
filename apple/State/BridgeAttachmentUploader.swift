import AgentBridgeAppleCore
import CoreGraphics
import CryptoKit
import Foundation
import ImageIO
import UniformTypeIdentifiers

public struct BridgeAttachmentUploadEndpoint: Sendable {
    public let capabilities: Set<String>
    public let isLocal: Bool
    public let validateConnection: @Sendable () async throws -> Void
    public let send: @Sendable (BridgeOperation) async throws -> JSONValue
    public let uploadBlob: @Sendable (URL, String?) async throws -> UUID

    public init(capabilities: Set<String>, isLocal: Bool, validateConnection: @escaping @Sendable () async throws -> Void,
                send: @escaping @Sendable (BridgeOperation) async throws -> JSONValue,
                uploadBlob: @escaping @Sendable (URL, String?) async throws -> UUID)
    {
        self.capabilities = capabilities
        self.isLocal = isLocal
        self.validateConnection = validateConnection
        self.send = send
        self.uploadBlob = uploadBlob
    }

    public var maximumBytes: Int {
        isLocal && capabilities.contains("sessions.attach.direct") || !isLocal && capabilities.contains("sessions.attach.blob")
            ? BridgeAttachment.maximumDirectBytes : BridgeAttachment.maximumBytes
    }
}

public struct BridgeUploadedAttachment: Sendable, Equatable {
    public let id: UUID
    public let name: String
    public let resized: Bool
}

public struct BridgeAttachmentUploadProgress: Sendable, Equatable {
    public let message: String
    public let fraction: Double?
}

public actor BridgeAttachmentUploader {
    private var uploading = false
    public init() {}

    public func upload(file url: URL, existingCount: Int, endpoint: BridgeAttachmentUploadEndpoint,
                       progress: @escaping @Sendable (BridgeAttachmentUploadProgress) async -> Void = { _ in }) async throws -> BridgeUploadedAttachment
    {
        guard !uploading else { throw BridgeOperationError("An attachment is already being prepared.") }
        guard (0 ..< 10).contains(existingCount) else { throw BridgeOperationError("A message can carry at most 10 attachments.") }
        guard endpoint.capabilities.contains("sessions.attach"), endpoint.capabilities.contains("sessions.reply") else { throw BridgeOperationError("This machine does not take attachments.") }
        uploading = true
        defer { uploading = false }
        let access = url.startAccessingSecurityScopedResource()
        defer {
            if access {
                url.stopAccessingSecurityScopedResource()
            }
        }
        try await endpoint.validateConnection()
        await progress(.init(message: "Preparing file…", fraction: nil))
        let prepared = try BridgeAttachmentImage.prepare(url, maximumBytes: endpoint.maximumBytes)
        defer { prepared.removeTemporaryFile() }
        try Task.checkCancellation()
        try await endpoint.validateConnection()
        if !endpoint.isLocal, prepared.size > BridgeAttachment.maximumBytes {
            await progress(.init(message: "Encrypting and uploading \(prepared.name)…", fraction: nil))
            let id = try await endpoint.uploadBlob(prepared.url, prepared.type)
            do {
                try Task.checkCancellation()
                try await endpoint.validateConnection()
            } catch {
                try? await discard(id: id, endpoint: endpoint)
                throw error
            }
            await progress(.init(message: "Attached \(prepared.name)", fraction: 1))
            return BridgeUploadedAttachment(id: id, name: prepared.name, resized: prepared.resized)
        }

        let input = try FileHandle(forReadingFrom: prepared.url)
        defer { try? input.close() }
        var hash = SHA256()
        var hashed = 0
        while let data = try input.read(upToCount: 64 * 1024), !data.isEmpty {
            try Task.checkCancellation()
            hashed += data.count
            guard hashed <= prepared.size else { throw BridgeOperationError("The file changed while it was being attached.") }
            hash.update(data: data)
        }
        guard hashed == prepared.size else { throw BridgeOperationError("The file changed while it was being attached.") }
        let id = UUID()
        let idString = id.uuidString.lowercased()
        let digest = Data(hash.finalize()).base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
        var metadata: [String: JSONValue] = ["attachment": .string(idString), "name": .string(prepared.name), "size": .number(Double(prepared.size)), "sha256": .string(digest)]
        if let type = prepared.type {
            metadata["type"] = .string(type)
        }
        do {
            try await endpoint.validateConnection()
            var state = try await BridgeAttachmentState(json: endpoint.send(.init(op: "attachment.start", data: metadata)))
            guard state.received <= prepared.size, !state.complete || state.received == prepared.size else { throw BridgeOperationError("The machine returned an invalid file offset.") }
            while !state.complete {
                try Task.checkCancellation()
                try await endpoint.validateConnection()
                guard state.received < prepared.size else { throw BridgeOperationError("The machine has not confirmed the file.") }
                try input.seek(toOffset: UInt64(state.received))
                let expected = min(BridgeAttachment.chunkBytes, prepared.size - state.received)
                var bytes = try readExactly(input, count: expected)
                var operation = chunk(id: idString, offset: state.received, bytes: bytes)
                if try JSONEncoder().encode(operation.request(id: Int.max)).count > 16 * 1024 {
                    bytes = Data(bytes.prefix(BridgeAttachment.chunkBytes / 2))
                    operation = chunk(id: idString, offset: state.received, bytes: bytes)
                }
                let expectedOffset = state.received + bytes.count
                state = try await BridgeAttachmentState(json: endpoint.send(operation))
                guard state.received == expectedOffset, !state.complete || state.received == prepared.size else { throw BridgeOperationError("The attachment acknowledgment did not match the bytes sent.") }
                await progress(.init(message: "Uploading \(prepared.name)", fraction: Double(state.received) / Double(prepared.size)))
            }
            try await endpoint.validateConnection()
            return BridgeUploadedAttachment(id: id, name: prepared.name, resized: prepared.resized)
        } catch {
            try? await discard(id: id, endpoint: endpoint)
            throw error
        }
    }

    public func discard(id: UUID, endpoint: BridgeAttachmentUploadEndpoint) async throws {
        try await endpoint.validateConnection()
        _ = try await endpoint.send(.init(op: "attachment.discard", data: ["attachment": .string(id.uuidString.lowercased())]))
    }

    private func chunk(id: String, offset: Int, bytes: Data) -> BridgeOperation {
        .init(op: "attachment.chunk", data: ["attachment": .string(id), "offset": .number(Double(offset)), "data": .string(bytes.base64EncodedString())])
    }

    private func readExactly(_ input: FileHandle, count: Int) throws -> Data {
        var bytes = Data()
        while bytes.count < count {
            guard let part = try input.read(upToCount: count - bytes.count), !part.isEmpty else { throw BridgeOperationError("The file changed while it was being attached.") }
            bytes.append(part)
        }
        return bytes
    }
}

public struct BridgePreparedAttachmentFile: Sendable {
    public let url: URL
    public let name: String
    public let type: String?
    public let size: Int
    public let resized: Bool
    private let temporaryDirectory: URL?

    fileprivate init(url: URL, name: String, type: String?, size: Int, resized: Bool, temporaryDirectory: URL? = nil) {
        self.url = url; self.name = name; self.type = type; self.size = size; self.resized = resized; self.temporaryDirectory = temporaryDirectory
    }

    public func removeTemporaryFile() {
        if let temporaryDirectory {
            try? FileManager.default.removeItem(at: temporaryDirectory)
        }
    }
}

public enum BridgeAttachmentImage {
    public static let maximumInputBytes = 100 * 1024 * 1024

    /// ImageIO downsamples from the source, avoiding a full-resolution UI bitmap.
    /// Oversized images become a bounded JPEG, on white like the Windows/PWA clients.
    public static func prepare(_ url: URL, maximumBytes: Int) throws -> BridgePreparedAttachmentFile {
        let values = try url.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey])
        guard values.isRegularFile == true, let size = values.fileSize, size > 0, size <= maximumInputBytes else {
            throw BridgeOperationError("Choose a regular file containing 1 to 100 MiB.")
        }
        let declaredType = UTType(filenameExtension: url.pathExtension)?.preferredMIMEType
        let source = CGImageSourceCreateWithURL(url as CFURL, [kCGImageSourceShouldCache: false] as CFDictionary)
        let mime = source.flatMap { CGImageSourceGetType($0) }.flatMap { UTType($0 as String)?.preferredMIMEType } ?? declaredType
        let imageTypes: Set = ["image/png", "image/jpeg", "image/gif", "image/webp", "image/heic", "image/heif"]
        let needsConversion = mime?.hasPrefix("image/") == true && !imageTypes.contains(mime ?? "")
        if size <= maximumBytes, !needsConversion {
            return .init(url: url, name: boundedName(url.lastPathComponent), type: mime, size: size, resized: false)
        }
        guard let source,
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? NSNumber,
              let height = properties[kCGImagePropertyPixelHeight] as? NSNumber,
              width.doubleValue > 0, height.doubleValue > 0, width.doubleValue * height.doubleValue <= 100_000_000
        else {
            throw BridgeOperationError("This file exceeds the connection limit and cannot be resized as an image.")
        }
        var edge = min(2048, max(width.intValue, height.intValue))
        for _ in 0 ..< 6 {
            try Task.checkCancellation()
            let options: [CFString: Any] = [kCGImageSourceCreateThumbnailFromImageAlways: true, kCGImageSourceCreateThumbnailWithTransform: true,
                                            kCGImageSourceThumbnailMaxPixelSize: edge, kCGImageSourceShouldCacheImmediately: true]
            guard let thumbnail = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary),
                  let context = CGContext(data: nil, width: thumbnail.width, height: thumbnail.height, bitsPerComponent: 8,
                                          bytesPerRow: thumbnail.width * 4, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)
            else {
                throw BridgeOperationError("This image could not be resized.")
            }
            context.setFillColor(CGColor(gray: 1, alpha: 1))
            context.fill(CGRect(x: 0, y: 0, width: thumbnail.width, height: thumbnail.height))
            context.draw(thumbnail, in: CGRect(x: 0, y: 0, width: thumbnail.width, height: thumbnail.height))
            guard let image = context.makeImage() else { throw BridgeOperationError("This image could not be resized.") }
            for quality in [0.85, 0.72, 0.6] {
                let output = NSMutableData()
                guard let destination = CGImageDestinationCreateWithData(output, UTType.jpeg.identifier as CFString, 1, nil) else { continue }
                CGImageDestinationAddImage(destination, image, [kCGImageDestinationLossyCompressionQuality: quality] as CFDictionary)
                guard CGImageDestinationFinalize(destination), output.length > 0, output.length <= maximumBytes else { continue }
                let directory = try temporaryDirectory()
                let name = boundedName(url.deletingPathExtension().lastPathComponent + ".jpg")
                let result = directory.appendingPathComponent(name)
                do { try (output as Data).write(to: result, options: .atomic) }
                catch { try? FileManager.default.removeItem(at: directory); throw error }
                return .init(url: result, name: name, type: "image/jpeg", size: output.length, resized: true, temporaryDirectory: directory)
            }
            edge = max(1, Int(Double(edge) * 0.7))
        }
        throw BridgeOperationError("The image could not fit this connection's attachment limit.")
    }

    public static func temporaryDirectory() throws -> URL {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("agent-bridge-import-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        return directory
    }

    private static func boundedName(_ name: String) -> String {
        let name = name.isEmpty ? "attachment" : name
        let ext = (name as NSString).pathExtension
        let suffix = !ext.isEmpty && ext.utf8.count <= 32 ? "." + ext : ""
        var stem = suffix.isEmpty ? name : (name as NSString).deletingPathExtension
        while (stem + suffix).utf8.count > 255, !stem.isEmpty {
            stem.removeLast()
        }
        return (stem.isEmpty ? "attachment" : stem) + suffix
    }
}
