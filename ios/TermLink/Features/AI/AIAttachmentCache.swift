// SPDX-License-Identifier: GPL-3.0-only
import CryptoKit
import Foundation

struct AIStagedAttachment: Codable, Identifiable, Equatable, Sendable {
    let id: String
    let pcID: UUID
    let sessionID: String
    let host: String
    let controlVersion: JSONValue
    let name: String
    let size: Int64
    let mime: String
    let digest: String
    var sent: Int64
    var status: String
    var error: String?

    var validMetadata: Bool {
        guard AIAttachmentCache.validID(id), UUID(uuidString: sessionID) != nil, UUID(uuidString: host) != nil,
              (1...AIAttachmentCache.maxFileBytes).contains(size), (0...size).contains(sent),
              !name.isEmpty, name.utf16.count <= 180, !name.contains(where: { "/\\\0".contains($0) }),
              !name.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }),
              !mime.isEmpty, mime.utf8.count <= 120,
              !mime.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }),
              digest.count == 64, digest.allSatisfy({ "0123456789abcdef".contains($0) }),
              ["uploading", "ready", "failed", "paused", "restored"].contains(status),
              case .number(let version) = controlVersion, version.isFinite, version > 0,
              version.rounded() == version, controlVersion.integer > 0 else { return false }
        return true
    }

    static func fileName(_ value: String) throws -> String {
        guard !value.isEmpty, !value.contains(where: { "/\\\0".contains($0) }),
              !value.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
            throw ClientError.invalidResponse("attachment filename")
        }
        var result = "", length = 0
        for character in value {
            let units = String(character).utf16.count
            if length + units > 180 { break }
            result.append(character); length += units
        }
        guard !result.isEmpty else { throw ClientError.invalidResponse("attachment filename") }
        return result
    }

    func isBound(to session: AIConversation, host currentHost: String) -> Bool {
        validMetadata && pcID == session.pcID && sessionID == session.id && host == currentHost &&
            controlVersion == session.raw["controlVersion"] && session.canControl && session.capabilities["attachments"].bool
    }

    /// Recovery is never an upload/send trigger, even if the last process saw "ready".
    static func recover(_ saved: [String: [Self]]) -> [String: [Self]] {
        var result: [String: [Self]] = [:], ids = Set<String>()
        for session in saved.keys.filter({ saved[$0]?.isEmpty == false }).sorted().prefix(64) where UUID(uuidString: session) != nil {
            var bytes: Int64 = 0
            for value in saved[session] ?? [] {
                guard value.sessionID == session, value.validMetadata, !ids.contains(value.id),
                      (result[session] ?? []).count < 5, bytes + value.size <= 50 * 1024 * 1024 else { continue }
                var recovered = value
                recovered.status = "restored"
                recovered.error = "已恢复附件，请确认当前会话后点击重试上传。"
                result[session, default: []].append(recovered)
                ids.insert(value.id); bytes += value.size
            }
        }
        return result
    }
}

/// Cache I/O and encryption run on this actor, off the UI actor. Files use random
/// IDs only; user filenames never select local paths. Content is never cached in plaintext.
actor AIAttachmentCache {
    static let maxFileBytes: Int64 = 20 * 1024 * 1024
    static let maxCacheBytes = 256 * 1024 * 1024
    nonisolated let directory: URL
    private let key: SymmetricKey
    private let scopeID: String

    init(scope: String, directory: URL, key: SymmetricKey) {
        scopeID = Data(SHA256.hash(data: Data(scope.utf8))).hex
        self.directory = directory; self.key = key
    }

    static func open(scope: String) throws -> AIAttachmentCache {
        let id = Data(SHA256.hash(data: Data(scope.utf8))).hex
        let store = KeychainStore(), account = "ai-upload-key:" + id
        let key: SymmetricKey
        if let raw = try store.read(account) {
            guard raw.count == 32 else { throw ClientError.authentication }
            key = SymmetricKey(data: raw)
        } else {
            key = SymmetricKey(size: .bits256)
            try store.write(key.withUnsafeBytes { Data($0) }, account: account)
        }
        let root = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask,
                                               appropriateFor: nil, create: true)
            .appendingPathComponent("AIUploads", isDirectory: true).appendingPathComponent(id, isDirectory: true)
        return Self(scope: scope, directory: root, key: key)
    }

    nonisolated static func validID(_ id: String) -> Bool {
        UUID(uuidString: id)?.uuidString.lowercased() == id
    }
    private func location(_ id: String) throws -> URL {
        guard Self.validID(id) else { throw ClientError.invalidResponse("attachment cache ID") }
        return directory.appendingPathComponent(id + ".sealed")
    }
    private func ensureDirectory() throws {
        let manager = FileManager.default
        if let attributes = try? manager.attributesOfItem(atPath: directory.path) {
            guard attributes[.type] as? FileAttributeType == .typeDirectory else { throw ClientError.invalidResponse("attachment cache directory") }
        } else {
            try manager.createDirectory(at: directory, withIntermediateDirectories: true,
                                        attributes: [.protectionKey: FileProtectionType.complete])
        }
        var root = directory, properties = URLResourceValues()
        properties.isExcludedFromBackup = true
        try root.setResourceValues(properties)
    }
    private func aad(_ id: String) -> Data { Data(("TERMLINK-IOS-AI-UPLOAD-V1\n" + scopeID + "\n" + id).utf8) }

    func stage(_ source: URL, id: String, expectedSize: Int64) throws -> String {
        guard (1...Self.maxFileBytes).contains(expectedSize) else { throw ClientError.limitExceeded }
        try Task.checkCancellation(); try ensureDirectory()
        let target = try location(id), manager = FileManager.default
        guard (try? manager.attributesOfItem(atPath: target.path)) == nil else { throw ClientError.invalidResponse("duplicate attachment cache ID") }
        let values = try source.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey])
        guard values.isRegularFile == true, values.isSymbolicLink != true, values.fileSize == Int(expectedSize) else {
            throw ClientError.invalidResponse("attachment source changed")
        }
        var used = 0
        for file in try manager.contentsOfDirectory(at: directory, includingPropertiesForKeys: [.fileSizeKey, .isRegularFileKey]) {
            let size = try file.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey])
            if size.isRegularFile == true { used += size.fileSize ?? 0 }
            guard used <= Self.maxCacheBytes else { throw ClientError.limitExceeded }
        }
        guard Int(expectedSize) + 28 <= Self.maxCacheBytes - used else { throw ClientError.limitExceeded }
        let file = try FileHandle(forReadingFrom: source); defer { try? file.close() }
        var bytes = Data(); bytes.reserveCapacity(Int(expectedSize))
        while let block = try file.read(upToCount: 65536), !block.isEmpty {
            try Task.checkCancellation()
            guard block.count <= Int(expectedSize) - bytes.count else { throw ClientError.invalidResponse("attachment source changed") }
            bytes.append(block)
        }
        guard bytes.count == Int(expectedSize) else { throw ClientError.invalidResponse("attachment source changed") }
        let digest = Data(SHA256.hash(data: bytes)).hex
        guard let sealed = try AES.GCM.seal(bytes, using: key, authenticating: aad(id)).combined else { throw ClientError.authentication }
        try Task.checkCancellation()
        try sealed.write(to: target, options: [.atomic, .completeFileProtection])
        return digest
    }

    func read(_ entry: AIStagedAttachment) throws -> Data {
        guard entry.validMetadata else { throw ClientError.invalidResponse("attachment metadata") }
        try Task.checkCancellation(); try ensureDirectory()
        let path = try location(entry.id)
        let attributes = try FileManager.default.attributesOfItem(atPath: path.path)
        guard attributes[.type] as? FileAttributeType == .typeRegular,
              (attributes[.size] as? NSNumber)?.int64Value == entry.size + 28 else {
            throw ClientError.invalidResponse("attachment cache size")
        }
        let sealed = try Data(contentsOf: path)
        let bytes = try AES.GCM.open(.init(combined: sealed), using: key, authenticating: aad(entry.id))
        guard bytes.count == Int(entry.size), Data(SHA256.hash(data: bytes)).hex == entry.digest else {
            throw ClientError.invalidResponse("attachment cache digest")
        }
        try Task.checkCancellation()
        return bytes
    }
    func remove(_ id: String) throws {
        let path = try location(id)
        if (try? FileManager.default.attributesOfItem(atPath: path.path)) != nil { try FileManager.default.removeItem(at: path) }
    }
    func prune(retaining ids: Set<String>) throws {
        try ensureDirectory()
        for path in try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil) {
            let id = path.deletingPathExtension().lastPathComponent
            if path.pathExtension == "sealed", Self.validID(id), ids.contains(id) { continue }
            let type = try FileManager.default.attributesOfItem(atPath: path.path)[.type] as? FileAttributeType
            if type == .typeRegular || type == .typeSymbolicLink { try FileManager.default.removeItem(at: path) }
        }
    }
}

enum AIAttachmentTransfer {
    /// Begin is idempotent and supplies the authoritative offset, including after
    /// a lost chunk/commit reply or process restart. Always retry the same ID/digest.
    @MainActor static func upload(_ record: AIStagedAttachment, bytes: Data, chunkBytes: Int,
                                  request: (String, [String: JSONValue]) async throws -> JSONValue,
                                  progress: (AIStagedAttachment) -> Void) async throws -> AIStagedAttachment {
        guard record.validMetadata, bytes.count == Int(record.size), (1...24576).contains(chunkBytes),
              Data(SHA256.hash(data: bytes)).hex == record.digest else { throw ClientError.invalidResponse("attachment upload content") }
        var entry = record; entry.status = "uploading"; entry.error = nil
        try Task.checkCancellation()
        let begin = try await request("attachment.begin", ["fileName": .string(entry.name), "mime": .string(entry.mime),
            "size": .number(Double(entry.size)), "sha256": .string(entry.digest)])
        func offset(_ value: JSONValue) throws -> Int64 {
            guard case .number(let value) = value, value.isFinite, value.rounded() == value,
                  value >= 0, value <= Double(entry.size) else { throw ClientError.protocolViolation("attachment offset") }
            return Int64(value)
        }
        entry.sent = try offset(begin["attachment"]["offset"])
        guard case .bool(let ready) = begin["attachment"]["ready"], !ready || entry.sent == entry.size else {
            throw ClientError.protocolViolation("attachment ready state")
        }
        progress(entry)
        if !ready {
            while entry.sent < entry.size {
                try Task.checkCancellation()
                let count = min(chunkBytes, Int(entry.size - entry.sent))
                let chunk = bytes.subdata(in: Int(entry.sent)..<Int(entry.sent) + count)
                let reply = try await request("attachment.chunk", ["offset": .number(Double(entry.sent)), "data": .string(chunk.base64EncodedString())])
                guard try offset(reply["attachment"]["offset"]) == entry.sent + Int64(count) else {
                    throw ClientError.protocolViolation("attachment acknowledgement")
                }
                entry.sent += Int64(count); progress(entry)
            }
            try Task.checkCancellation()
            let committed = try await request("attachment.commit", [:])
            guard committed["attachment"]["ready"] == .bool(true) else { throw ClientError.protocolViolation("attachment commit") }
        }
        entry.status = "ready"; entry.error = nil; progress(entry)
        return entry
    }
}
