// SPDX-License-Identifier: GPL-3.0-only
import CryptoKit
import Darwin
import Foundation

/// Account-private metadata and independently authenticated 16 KiB frames.
/// Remote filenames never choose cache paths. Plaintext exists only during an
/// explicit share export, streamed to a protected temporary file.
actor AIArtifactDownloadCache {
    static let frameBytes = 16 * 1024
    static let maximumJobs = 32
    static let maximumCacheBytes: Int64 = 256 * 1024 * 1024
    nonisolated let directory: URL
    private let key: SymmetricKey
    private let scopeID: String
    private var records: [UUID: AIArtifactDownload] = [:]
    private var loaded = false

    init(scope: String, directory: URL, key: SymmetricKey) {
        self.directory = directory; self.key = key
        scopeID = Data(SHA256.hash(data: Data(scope.utf8))).hex
    }
    static func open(scope: String) throws -> AIArtifactDownloadCache {
        let id = Data(SHA256.hash(data: Data(scope.utf8))).hex
        let store = KeychainStore(), account = "ai-artifact-download-key:" + id
        let key: SymmetricKey
        if let raw = try store.read(account) {
            guard raw.count == 32 else { throw ClientError.authentication }; key = SymmetricKey(data: raw)
        } else {
            key = SymmetricKey(size: .bits256)
            try store.write(key.withUnsafeBytes { Data($0) }, account: account)
        }
        let parent = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
        return .init(scope: scope, directory: parent.appendingPathComponent("AIArtifactDownloads", isDirectory: true)
            .appendingPathComponent(id, isDirectory: true), key: key)
    }
    private func root() throws -> Int32 {
        let manager = FileManager.default
        if let a = try? manager.attributesOfItem(atPath: directory.path) {
            guard a[.type] as? FileAttributeType == .typeDirectory else { throw ClientError.invalidResponse("artifact cache directory") }
        } else {
            try manager.createDirectory(at: directory, withIntermediateDirectories: true,
                attributes: [.posixPermissions: 0o700, .protectionKey: FileProtectionType.complete])
        }
        var url = directory, properties = URLResourceValues(); properties.isExcludedFromBackup = true
        try url.setResourceValues(properties)
        let fd = Darwin.open(directory.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { throw ClientError.unavailable("下载缓存暂不可用。") }
        var info = stat()
        guard fstat(fd, &info) == 0, info.st_uid == getuid(), fchmod(fd, 0o700) == 0 else {
            Darwin.close(fd); throw ClientError.invalidResponse("artifact cache owner")
        }
        return fd
    }
    private func file(_ name: String, root: Int32, flags: Int32) throws -> FileHandle {
        let fd = openat(root, name, flags | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard fd >= 0 else { throw ClientError.unavailable("下载缓存文件不可用，请删除记录后重新下载。") }
        var info = stat()
        guard fstat(fd, &info) == 0, info.st_mode & S_IFMT == S_IFREG, info.st_nlink == 1,
              info.st_uid == getuid(), fchmod(fd, 0o600) == 0 else {
            Darwin.close(fd); throw ClientError.invalidResponse("artifact cache file")
        }
        return FileHandle(fileDescriptor: fd, closeOnDealloc: true)
    }
    private func name(_ id: UUID) -> String { id.uuidString.lowercased() + ".part" }
    private var metadataAAD: Data { Data(("TERMLINK-IOS-AI-DOWNLOAD-METADATA-V1\n" + scopeID).utf8) }
    private func frameAAD(_ record: AIArtifactDownload, offset: Int64) throws -> Data {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        let binding = Data(SHA256.hash(data: try encoder.encode(record.source))).hex
        return Data(("TERMLINK-IOS-AI-DOWNLOAD-FRAME-V1\n" + scopeID + "\n" + record.id.uuidString.lowercased() + "\n" + binding + "\n" + String(offset)).utf8)
    }
    private func readExactly(_ handle: FileHandle, count: Int) throws -> Data {
        var data = Data()
        while data.count < count {
            guard let next = try handle.read(upToCount: count - data.count), !next.isEmpty else {
                throw ClientError.invalidResponse("artifact cache truncated")
            }
            data.append(next)
        }
        return data
    }
    private func persist(root fd: Int32) throws {
        let data = try JSONEncoder().encode(records.values.sorted { $0.id.uuidString < $1.id.uuidString })
        guard data.count <= 256 * 1024, let sealed = try AES.GCM.seal(data, using: key, authenticating: metadataAAD).combined else {
            throw ClientError.limitExceeded
        }
        let temporary = UUID().uuidString.lowercased() + ".meta-tmp"
        let output = try file(temporary, root: fd, flags: O_WRONLY | O_CREAT | O_EXCL)
        do {
            try output.write(contentsOf: sealed); try output.synchronize(); try output.close()
            guard renameat(fd, temporary, fd, "metadata.sealed") == 0 else { throw ClientError.unavailable("无法保存下载记录。") }
            _ = fsync(fd)
        } catch { try? output.close(); _ = unlinkat(fd, temporary, 0); throw error }
    }
    func restore() throws -> [AIArtifactDownload] {
        if loaded { return Array(records.values) }
        let fd = try root(); defer { Darwin.close(fd) }
        var info = stat()
        if fstatat(fd, "metadata.sealed", &info, AT_SYMLINK_NOFOLLOW) == 0 {
            guard info.st_size <= 256 * 1024 + 28 else { throw ClientError.limitExceeded }
            let input = try file("metadata.sealed", root: fd, flags: O_RDONLY); defer { try? input.close() }
            let sealed = try readExactly(input, count: Int(info.st_size))
            let data = try AES.GCM.open(.init(combined: sealed), using: key, authenticating: metadataAAD)
            let items = try JSONDecoder().decode([AIArtifactDownload].self, from: data)
            guard items.count <= Self.maximumJobs else { throw ClientError.limitExceeded }
            var next: [UUID: AIArtifactDownload] = [:]
            for var item in items {
                try item.validate(); guard next[item.id] == nil else { throw ClientError.invalidResponse("duplicate artifact download") }
                if item.status.active { item.status = .restored; item.error = "请重新连接原会话后明确重试。" }
                next[item.id] = item
            }
            records = next
        } else if errno != ENOENT { throw ClientError.unavailable("无法读取下载记录。") }
        // Recover publication crashes without following arbitrary filesystem entries.
        for entry in try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil) {
            let filename = entry.lastPathComponent
            let orphanPart = filename.hasSuffix(".part") && UUID(uuidString: String(filename.dropLast(5))).map { records[$0] == nil } == true
            let staleMetadata = filename.hasSuffix(".meta-tmp") && UUID(uuidString: String(filename.dropLast(9))) != nil
            guard orphanPart || staleMetadata else { continue }
            var value = stat()
            guard fstatat(fd, filename, &value, AT_SYMLINK_NOFOLLOW) == 0,
                  value.st_mode & S_IFMT == S_IFREG, value.st_nlink == 1, value.st_uid == getuid() else {
                throw ClientError.invalidResponse("artifact cache orphan")
            }
            guard unlinkat(fd, filename, 0) == 0 else { throw ClientError.unavailable("无法清理未完成的下载缓存。") }
        }
        loaded = true
        do { try persist(root: fd) } catch { loaded = false; throw error }
        return Array(records.values)
    }
    func insert(source: AIArtifactSource) throws -> AIArtifactDownload {
        _ = try restore(); try source.validate(); guard records.count < Self.maximumJobs else { throw ClientError.limitExceeded }
        let fd = try root(); defer { Darwin.close(fd) }
        let value = AIArtifactDownload(id: UUID(), source: source, createdAt: Date())
        let output = try file(name(value.id), root: fd, flags: O_RDWR | O_CREAT | O_EXCL); try output.close()
        records[value.id] = value
        do { try persist(root: fd) } catch { records.removeValue(forKey: value.id); _ = unlinkat(fd, name(value.id), 0); throw error }
        return value
    }
    private func get(_ id: UUID) throws -> AIArtifactDownload {
        guard loaded, let value = records[id] else { throw ClientError.invalidResponse("artifact download missing") }
        return value
    }
    /// Validate every committed encrypted frame before sending a resume offset.
    private func scan(_ record: AIArtifactDownload, input: FileHandle, output: FileHandle? = nil) throws {
        try input.seek(toOffset: 0)
        var logical: Int64 = 0, physical: Int64 = 0
        while physical < record.sealedBytes {
            try Task.checkCancellation()
            let header = try readExactly(input, count: 4)
            let count = header.reduce(UInt32(0)) { ($0 << 8) | UInt32($1) }
            guard (1...Self.frameBytes).contains(Int(count)), Int64(count) <= record.received - logical,
                  physical + 4 + Int64(count) + 28 <= record.sealedBytes else { throw ClientError.invalidResponse("artifact cache frame") }
            let sealed = try readExactly(input, count: Int(count) + 28)
            let plain = try AES.GCM.open(.init(combined: sealed), using: key, authenticating: frameAAD(record, offset: logical))
            guard plain.count == Int(count) else { throw ClientError.invalidResponse("artifact cache frame") }
            try output?.write(contentsOf: plain)
            logical += Int64(count); physical += 4 + Int64(count) + 28
        }
        guard logical == record.received, physical == record.sealedBytes else { throw ClientError.invalidResponse("artifact cache prefix") }
    }
    func resume(_ id: UUID) throws -> AIArtifactDownload {
        var record = try get(id)
        guard record.status != .completed else { throw ClientError.readOnly }
        let fd = try root(); defer { Darwin.close(fd) }
        let input = try file(name(id), root: fd, flags: O_RDWR); defer { try? input.close() }
        guard try input.seekToEnd() >= UInt64(record.sealedBytes) else { throw ClientError.invalidResponse("artifact cache prefix") }
        try scan(record, input: input)
        // A crash after fsync but before metadata commit may leave an uncommitted tail.
        try input.truncate(atOffset: UInt64(record.sealedBytes)); try input.synchronize()
        record.status = .downloading; record.error = nil; records[id] = record
        try persist(root: fd); return record
    }
    func append(_ id: UUID, chunk: AIArtifactChunk) throws -> AIArtifactDownload {
        var record = try get(id)
        guard record.status == .downloading, chunk.nextOffset == record.received + Int64(chunk.bytes.count),
              record.expectedBytes == nil || record.expectedBytes == chunk.size,
              chunk.size >= 0, chunk.size <= AIArtifactChunk.maximumFileBytes,
              chunk.bytes.count <= AIArtifactChunk.maximumPageBytes, chunk.nextOffset <= chunk.size else { throw ClientError.protocolViolation("artifact cache offset") }
        let fd = try root(); defer { Darwin.close(fd) }
        // Charge actual encrypted files, including any uncommitted crash tails.
        var used: Int64 = 0
        for item in try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil) {
            var info = stat()
            guard fstatat(fd, item.lastPathComponent, &info, AT_SYMLINK_NOFOLLOW) == 0,
                  info.st_mode & S_IFMT == S_IFREG, info.st_nlink == 1 else { throw ClientError.invalidResponse("artifact cache entry") }
            used += info.st_size
        }
        let added = Int64(chunk.bytes.count + ((chunk.bytes.count + Self.frameBytes - 1) / Self.frameBytes) * 32)
        guard used <= Self.maximumCacheBytes, added <= Self.maximumCacheBytes - used,
              added <= AIArtifactChunk.maximumFileBytes + 1024 * 1024 - record.sealedBytes else { throw ClientError.limitExceeded }
        let output = try file(name(id), root: fd, flags: O_RDWR); defer { try? output.close() }
        guard try output.seekToEnd() == UInt64(record.sealedBytes) else { throw ClientError.invalidResponse("artifact cache tail") }
        let old = record
        do {
            var at = 0
            while at < chunk.bytes.count {
                try Task.checkCancellation()
                let count = min(Self.frameBytes, chunk.bytes.count - at), plain = chunk.bytes.subdata(in: at..<(at + count))
                guard let sealed = try AES.GCM.seal(plain, using: key, authenticating: frameAAD(record, offset: record.received)).combined else { throw ClientError.authentication }
                var length = UInt32(count).bigEndian
                try output.write(contentsOf: withUnsafeBytes(of: &length) { Data($0) }); try output.write(contentsOf: sealed)
                record.received += Int64(count); record.sealedBytes += Int64(4 + sealed.count); at += count
            }
            try output.synchronize(); record.expectedBytes = chunk.size
            record.status = record.received == chunk.size ? .completed : .downloading
            records[id] = record
            do { try persist(root: fd) } catch { records[id] = old; throw error }
            return record
        } catch {
            try? output.truncate(atOffset: UInt64(old.sealedBytes)); try? output.synchronize(); throw error
        }
    }
    func stop(_ id: UUID, status: AIArtifactDownload.Status, error: String? = nil) throws -> AIArtifactDownload {
        guard [.paused, .cancelled, .failed].contains(status) else { throw ClientError.invalidResponse("artifact stop status") }
        var record = try get(id); if record.status == .completed { return record }
        let old = record; record.status = status; record.error = error
        let fd = try root(); defer { Darwin.close(fd) }; records[id] = record
        do { try persist(root: fd) } catch { records[id] = old; throw error }; return record
    }
    func remove(_ id: UUID) throws {
        let record = try get(id); guard !record.status.active else { throw ClientError.readOnly }
        let fd = try root(); defer { Darwin.close(fd) }
        var info = stat()
        let exists = fstatat(fd, name(id), &info, AT_SYMLINK_NOFOLLOW) == 0
        if exists { let input = try file(name(id), root: fd, flags: O_RDONLY); try input.close() }
        else if errno != ENOENT { throw ClientError.unavailable("无法检查下载缓存。") }
        records.removeValue(forKey: id)
        do { try persist(root: fd) } catch { records[id] = record; throw error }
        if exists, unlinkat(fd, name(id), 0) != 0 { throw ClientError.unavailable("下载记录已移除，但缓存清理未完成。") }
    }
    func export(_ id: UUID) throws -> URL {
        let record = try get(id)
        guard record.status == .completed, record.expectedBytes == record.received else { throw ClientError.readOnly }
        let fd = try root(); defer { Darwin.close(fd) }
        let input = try file(name(id), root: fd, flags: O_RDONLY); defer { try? input.close() }
        guard try input.seekToEnd() == UInt64(record.sealedBytes) else { throw ClientError.invalidResponse("artifact cache tail") }
        let parent = FileManager.default.temporaryDirectory.appendingPathComponent("AIArtifactShares-" + scopeID, isDirectory: true)
            .appendingPathComponent(UUID().uuidString.lowercased(), isDirectory: true)
        try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700, .protectionKey: FileProtectionType.complete])
        var url = parent, properties = URLResourceValues(); properties.isExcludedFromBackup = true; try url.setResourceValues(properties)
        let destination = parent.appendingPathComponent(record.source.filename)
        guard FileManager.default.createFile(atPath: destination.path, contents: nil,
            attributes: [.posixPermissions: 0o600, .protectionKey: FileProtectionType.complete]) else { throw ClientError.unavailable("无法导出下载文件。") }
        do {
            let output = try FileHandle(forWritingTo: destination); defer { try? output.close() }
            try scan(record, input: input, output: output); try output.synchronize(); return destination
        } catch { try? FileManager.default.removeItem(at: parent); throw error }
    }
    func discardExport(_ url: URL) throws {
        let parent = FileManager.default.temporaryDirectory.appendingPathComponent("AIArtifactShares-" + scopeID, isDirectory: true).standardizedFileURL
        let directory = url.deletingLastPathComponent().standardizedFileURL
        guard directory.deletingLastPathComponent() == parent, UUID(uuidString: directory.lastPathComponent) != nil else { throw ClientError.readOnly }
        if FileManager.default.fileExists(atPath: directory.path) { try FileManager.default.removeItem(at: directory) }
    }
}
