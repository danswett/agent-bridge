import CryptoKit
import Foundation

/// An upload contains ciphertext only. The ticket key travels inside the Noise
/// channel; it is never in an HTTP URL, header or relay control message.
public enum RelayBlobUpload {
    public static func prepare(file: URL, name: String, type: String?, recipient: String,
                               relay: RelayBridge, origin: URL) async throws -> (BridgeBlobAttachment, String, Data)
    {
        let size = try file.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
        guard size > 256 * 1024, size <= BridgeBlob.maximumAttachmentBytes else { throw RelayError.invalidMessage }
        let slot = try await relay.blobPut(recipient: recipient, plaintextSize: size)
        guard let ticket = slot["blob"]?.string, let url = slot["url"]?.string else { throw RelayError.invalidMessage }
        do {
            let destination = try BridgeBlob.resolveURL(url, relay: origin)
            let key = BridgeBlob.newKey()
            let id = try BridgeBlob.ticketID(ticket)
            let directory = FileManager.default.temporaryDirectory.appendingPathComponent("agent-bridge-upload-\(UUID().uuidString)", isDirectory: true)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
            defer { try? FileManager.default.removeItem(at: directory) }
            let encrypted = directory.appendingPathComponent("ciphertext")
            guard FileManager.default.createFile(atPath: encrypted.path, contents: nil, attributes: [.posixPermissions: 0o600]) else { throw RelayError.invalidMessage }
            let input = try FileHandle(forReadingFrom: file)
            let output = try FileHandle(forWritingTo: encrypted)
            defer { try? input.close(); try? output.close() }
            var hash = SHA256()
            var counted = 0
            while let chunk = try input.read(upToCount: 65536), !chunk.isEmpty {
                try Task.checkCancellation()
                counted += chunk.count
                guard counted <= size else { throw RelayError.refused("The file changed while it was being read.") }
                hash.update(data: chunk)
            }
            guard counted == size else { throw RelayError.refused("The file changed while it was being read.") }
            let attachment = try BridgeBlobAttachment(name: name, type: type, size: size, sha256: Data(hash.finalize()))
            try input.seek(toOffset: 0)
            try BridgeBlob.seal(input: input, plaintextSize: size, output: output, key: key, id: id)
            try output.synchronize()
            try Task.checkCancellation()
            try await CiphertextUpload.upload(file: encrypted, destination: destination)
            return (attachment, ticket, key)
        } catch {
            try? await relay.blobDelete(ticket: ticket)
            throw error
        }
    }
}

/// URLSession's delegate discards the response incrementally, so an untrusted
/// storage response cannot allocate an unbounded body. Redirects are refused.
private final class CiphertextUpload: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Void, any Error>?
    private var task: URLSessionUploadTask?
    private var session: URLSession?
    private var count = 0
    private var status = 0
    private var cancelled = false

    static func upload(file: URL, destination: URL) async throws {
        let upload = CiphertextUpload()
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in upload.start(file, destination: destination, continuation: continuation) }
        } onCancel: { upload.cancel() }
    }

    private func start(_ file: URL, destination: URL, continuation: CheckedContinuation<Void, any Error>) {
        lock.lock()
        if cancelled {
            lock.unlock(); continuation.resume(throwing: CancellationError()); return
        }
        self.continuation = continuation
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 120
        configuration.timeoutIntervalForResource = 12 * 60
        let session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
        self.session = session
        var request = URLRequest(url: destination)
        request.httpMethod = "PUT"
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        request.setValue("BlockBlob", forHTTPHeaderField: "x-ms-blob-type")
        let task = session.uploadTask(with: request, fromFile: file)
        self.task = task
        lock.unlock()
        task.resume()
    }

    private func cancel() {
        lock.lock(); cancelled = true; let task = task; lock.unlock()
        task?.cancel()
    }

    func urlSession(_: URLSession, dataTask _: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping @Sendable (URLSession.ResponseDisposition) -> Void)
    {
        lock.lock(); status = (response as? HTTPURLResponse)?.statusCode ?? 0; lock.unlock()
        completionHandler(.allow)
    }

    func urlSession(_: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        lock.lock(); count += data.count; let tooLarge = count > 16384; lock.unlock()
        if tooLarge {
            dataTask.cancel()
        }
    }

    func urlSession(_: URLSession, task _: URLSessionTask, willPerformHTTPRedirection _: HTTPURLResponse,
                    newRequest _: URLRequest, completionHandler: @escaping @Sendable (URLRequest?) -> Void)
    {
        completionHandler(nil)
    }

    func urlSession(_ session: URLSession, task _: URLSessionTask, didCompleteWithError error: (any Error)?) {
        lock.lock()
        let continuation = continuation
        self.continuation = nil
        task = nil
        self.session = nil
        let success = (200 ... 299).contains(status) && count <= 16384
        lock.unlock()
        session.finishTasksAndInvalidate()
        if let error {
            continuation?.resume(throwing: error)
        } else if !success {
            continuation?.resume(throwing: RelayError.refused("The encrypted file upload failed."))
        } else {
            continuation?.resume()
        }
    }
}
