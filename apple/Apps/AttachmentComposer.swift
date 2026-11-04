import AgentBridgeAppleCore
import CoreTransferable
import PhotosUI
import SwiftUI
import UniformTypeIdentifiers
#if os(macOS)
    import AppKit
#endif

@MainActor
struct AttachmentComposer: View {
    let store: BridgeStore
    let host: HostStore
    @Binding var attachments: [(UUID, String)]
    var disabled = false
    var onBusy: (Bool) -> Void = { _ in }
    @State private var uploader = BridgeAttachmentUploader()
    @State private var importing = false
    @State private var photo: PhotosPickerItem?
    @State private var work: Task<Void, Never>?
    @State private var busy = false
    @State private var message = ""
    @State private var fraction: Double?
    @State private var error: String?

    var body: some View {
        ForEach(attachments, id: \.0) { attachment in
            HStack {
                Label(attachment.1, systemImage: "paperclip")
                    .lineLimit(2)
                Spacer()
                Button("Remove", systemImage: "xmark.circle") { remove(attachment.0) }
                    .labelStyle(.iconOnly)
                    .accessibilityLabel("Remove \(attachment.1)")
                    .disabled(disabled || busy)
            }
        }
        if busy {
            if let fraction {
                ProgressView(message, value: fraction)
            } else {
                ProgressView(message)
            }
        }
        if host.can("sessions.attach") {
            HStack {
                Button("Attach File", systemImage: "paperclip") { importing = true }
                PhotosPicker(selection: $photo, matching: .images) { Label("Photo", systemImage: "photo") }
                #if os(macOS)
                    Button("Paste Image", systemImage: "clipboard") { pasteImage() }
                #endif
            }
            .disabled(disabled || busy || !host.isLive || attachments.count >= 10)
            Text("Up to 10 files. Images are reduced when needed to fit this connection.")
                .font(.caption).foregroundStyle(.secondary)
        }
        if let error {
            Text(error).font(.caption).foregroundStyle(.red)
        }
        Color.clear.frame(height: 0)
            .fileImporter(isPresented: $importing, allowedContentTypes: [.item]) { result in
                switch result {
                case let .success(url): start { ImportedAttachment(url: url, temporaryDirectory: nil) }
                case let .failure(failure): error = failure.localizedDescription
                }
            }
            .onChange(of: photo) {
                guard let photo else { return }
                self.photo = nil
                start {
                    guard let image = try await photo.loadTransferable(type: ImportedPhoto.self) else { throw BridgeOperationError("The photo could not be imported.") }
                    return ImportedAttachment(url: image.url, temporaryDirectory: image.directory)
                }
            }
            .onChange(of: host.connectionID) {
                work?.cancel()
                attachments = []
            }
            .onDisappear { work?.cancel() }
    }

    private func endpoint(connection: UUID) -> BridgeAttachmentUploadEndpoint {
        BridgeAttachmentUploadEndpoint(capabilities: host.capabilities, isLocal: host.isLocal, validateConnection: {
            try await MainActor.run {
                guard host.isLive, host.connectionID == connection else { throw BridgeOperationError("The machine connection changed. Attach the file again.") }
            }
        }, send: { operation in
            try await host.call(operation.op, data: operation.data, onlyOn: connection)
        }, uploadBlob: { url, type in
            try await store.uploadBlob(url, type: type, to: host, onlyOn: connection)
        })
    }

    private func start(_ source: @escaping @Sendable () async throws -> ImportedAttachment) {
        guard !busy, !disabled, attachments.count < 10 else { return }
        let connection = host.connectionID
        let endpoint = endpoint(connection: connection)
        let count = attachments.count
        busy = true
        onBusy(true)
        message = "Importing file…"
        fraction = nil
        error = nil
        work = Task {
            defer { busy = false; onBusy(false); work = nil }
            do {
                let imported = try await source()
                defer { imported.removeTemporaryFile() }
                try Task.checkCancellation()
                let file = try await uploader.upload(file: imported.url, existingCount: count, endpoint: endpoint) { update in
                    await MainActor.run { message = update.message; fraction = update.fraction }
                }
                do {
                    try Task.checkCancellation()
                    guard host.connectionID == connection else { throw BridgeOperationError("The machine connection changed. Attach the file again.") }
                    attachments.append((file.id, file.name + (file.resized ? " (resized)" : "")))
                } catch {
                    // The view can disappear after upload completion but before taking
                    // ownership of the staged ID. Do not leave that file behind.
                    try? await Task { try await uploader.discard(id: file.id, endpoint: endpoint) }.value
                    throw error
                }
            } catch is CancellationError {
                error = nil
            } catch { self.error = error.localizedDescription }
        }
    }

    private func remove(_ id: UUID) {
        guard !busy, !disabled else { return }
        let endpoint = endpoint(connection: host.connectionID)
        busy = true
        onBusy(true)
        work = Task {
            defer { busy = false; onBusy(false); work = nil }
            do {
                try await uploader.discard(id: id, endpoint: endpoint)
                attachments.removeAll { $0.0 == id }
            } catch { self.error = error.localizedDescription }
        }
    }

    #if os(macOS)
        private func pasteImage() {
            guard !busy, !disabled else { return }
            let types: [(NSPasteboard.PasteboardType, String)] = [(.png, "png"), (.init("public.jpeg"), "jpg"), (.tiff, "tiff")]
            do {
                guard let image = types.lazy.compactMap({ type, ext in NSPasteboard.general.data(forType: type).map { ($0, ext) } }).first else {
                    throw BridgeOperationError("The clipboard does not contain an image.")
                }
                guard !image.0.isEmpty, image.0.count <= BridgeAttachmentImage.maximumInputBytes else { throw BridgeOperationError("The clipboard image exceeds 100 MiB.") }
                start { try await ClipboardImageImport.shared.write(image.0, extension: image.1) }
            } catch { self.error = error.localizedDescription }
        }
    #endif
}

/// Pasteboard access stays on the UI actor; writing up to 100 MiB must not.
private actor ClipboardImageImport {
    static let shared = ClipboardImageImport()

    func write(_ data: Data, extension ext: String) throws -> ImportedAttachment {
        try Task.checkCancellation()
        let directory = try BridgeAttachmentImage.temporaryDirectory()
        let url = directory.appendingPathComponent("image." + ext)
        do {
            try data.write(to: url, options: .atomic)
            try Task.checkCancellation()
            return ImportedAttachment(url: url, temporaryDirectory: directory)
        } catch {
            try? FileManager.default.removeItem(at: directory)
            throw error
        }
    }
}

private struct ImportedAttachment: Sendable {
    let url: URL
    let temporaryDirectory: URL?
    func removeTemporaryFile() {
        if let temporaryDirectory {
            try? FileManager.default.removeItem(at: temporaryDirectory)
        }
    }
}

private struct ImportedPhoto: Transferable, Sendable {
    let url: URL
    let directory: URL

    static var transferRepresentation: some TransferRepresentation {
        FileRepresentation(importedContentType: .image) { received in
            let size = try received.file.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
            guard size > 0, size <= BridgeAttachmentImage.maximumInputBytes else { throw BridgeOperationError("The photo exceeds 100 MiB.") }
            let directory = try BridgeAttachmentImage.temporaryDirectory()
            let file = directory.appendingPathComponent("image" + (received.file.pathExtension.isEmpty ? "" : "." + received.file.pathExtension))
            do { try FileManager.default.copyItem(at: received.file, to: file) }
            catch { try? FileManager.default.removeItem(at: directory); throw error }
            return ImportedPhoto(url: file, directory: directory)
        }
    }
}
