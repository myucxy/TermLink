// SPDX-License-Identifier: GPL-3.0-only
import CryptoKit
import XCTest
@testable import TermLink

final class AIAttachmentCacheTests: XCTestCase {
    private let key = SymmetricKey(data: Data(repeating: 0x71, count: 32))
    private func record(id: String = UUID().uuidString.lowercased(), bytes: Data, status: String = "ready") -> AIStagedAttachment {
        AIStagedAttachment(id: id, pcID: UUID(), sessionID: UUID().uuidString.lowercased(), host: UUID().uuidString.lowercased(),
            controlVersion: .number(3), name: "private-photo.png", size: Int64(bytes.count), mime: "image/png",
            digest: Data(SHA256.hash(data: bytes)).hex, sent: Int64(bytes.count), status: status)
    }
    private func temporary() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }
    private func cannotRead(_ cache: AIAttachmentCache, _ entry: AIStagedAttachment, file: StaticString = #filePath, line: UInt = #line) async {
        do { _ = try await cache.read(entry); XCTFail("Untrusted cached content was accepted", file: file, line: line) } catch {}
    }

    func testEncryptedStagingSurvivesOriginalRemovalAndCacheRecreation() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let bytes = Data(("private-attachment-content-" + String(repeating: "中文", count: 100)).utf8)
        let source = root.appendingPathComponent("source.png"), directory = root.appendingPathComponent("cache")
        try bytes.write(to: source)
        let entry = record(bytes: bytes), cache = AIAttachmentCache(scope: "account-A", directory: directory, key: key)
        let digest = try await cache.stage(source, id: entry.id, expectedSize: entry.size)
        XCTAssertEqual(digest, entry.digest)
        let sealed = try Data(contentsOf: directory.appendingPathComponent(entry.id + ".sealed"))
        XCTAssertNil(sealed.range(of: bytes))
        XCTAssertEqual(sealed.count, bytes.count + 28)
        try FileManager.default.removeItem(at: source)
        let reopened = AIAttachmentCache(scope: "account-A", directory: directory, key: key)
        let metadata = try JSONEncoder().encode([entry.sessionID: [entry]])
        let decoded = try JSONDecoder().decode([String: [AIStagedAttachment]].self, from: metadata)
        let recovered = try XCTUnwrap(AIStagedAttachment.recover(decoded)[entry.sessionID]?.first)
        XCTAssertEqual(recovered.status, "restored")
        let restored = try await reopened.read(recovered)
        XCTAssertEqual(restored, bytes)
        let excluded = try directory.resourceValues(forKeys: [.isExcludedFromBackupKey])
        XCTAssertEqual(excluded.isExcludedFromBackup, true)
        try await reopened.remove(entry.id)
        await cannotRead(reopened, entry)
    }

    func testCacheRejectsOtherAccountKeyOrAttachmentIDEvenWithIdenticalMetadata() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let bytes = Data("account-bound fixture".utf8), entry = record(bytes: Data("account-bound fixture".utf8))
        let source = root.appendingPathComponent("source"), directory = root.appendingPathComponent("cache")
        try bytes.write(to: source)
        let cache = AIAttachmentCache(scope: "account-A", directory: directory, key: key)
        _ = try await cache.stage(source, id: entry.id, expectedSize: entry.size)
        await cannotRead(AIAttachmentCache(scope: "account-B", directory: directory, key: key), entry)
        await cannotRead(AIAttachmentCache(scope: "account-A", directory: directory,
            key: SymmetricKey(data: Data(repeating: 0x72, count: 32))), entry)
        let other = record(bytes: bytes)
        try FileManager.default.copyItem(at: directory.appendingPathComponent(entry.id + ".sealed"),
                                        to: directory.appendingPathComponent(other.id + ".sealed"))
        await cannotRead(cache, other)
    }

    func testTamperingTruncationAndChangedDigestAreRejectedBeforeUpload() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let bytes = Data(repeating: 91, count: 65537), entry = record(bytes: Data(repeating: 91, count: 65537))
        let source = root.appendingPathComponent("source"), directory = root.appendingPathComponent("cache")
        try bytes.write(to: source)
        let cache = AIAttachmentCache(scope: "A", directory: directory, key: key)
        _ = try await cache.stage(source, id: entry.id, expectedSize: entry.size)
        let target = directory.appendingPathComponent(entry.id + ".sealed"), sealed = try Data(contentsOf: directory.appendingPathComponent(entry.id + ".sealed"))
        var changed = sealed; changed[changed.count / 2] ^= 1; try changed.write(to: target)
        await cannotRead(cache, entry)
        try Data(sealed.dropLast()).write(to: target); await cannotRead(cache, entry)
        try sealed.write(to: target)
        let wrong = AIStagedAttachment(id: entry.id, pcID: entry.pcID, sessionID: entry.sessionID, host: entry.host,
            controlVersion: entry.controlVersion, name: entry.name, size: entry.size, mime: entry.mime,
            digest: String(repeating: "0", count: 64), sent: 0, status: "failed")
        await cannotRead(cache, wrong)
    }

    func testPathTraversalSymlinksDuplicateIDsAndSourceSizeChangesAreRejected() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let bytes = Data("safe".utf8), entry = record(bytes: Data("safe".utf8))
        let source = root.appendingPathComponent("source"), directory = root.appendingPathComponent("cache")
        try bytes.write(to: source)
        let cache = AIAttachmentCache(scope: "A", directory: directory, key: key)
        for (id, size) in [("../outside", entry.size), (entry.id, 0), (entry.id, AIAttachmentCache.maxFileBytes + 1), (entry.id, entry.size + 1)] {
            do { _ = try await cache.stage(source, id: id, expectedSize: size); XCTFail("unsafe stage accepted") } catch {}
        }
        let link = root.appendingPathComponent("link")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: source)
        do { _ = try await cache.stage(link, id: entry.id, expectedSize: entry.size); XCTFail("source symlink accepted") } catch {}
        _ = try await cache.stage(source, id: entry.id, expectedSize: entry.size)
        do { _ = try await cache.stage(source, id: entry.id, expectedSize: entry.size); XCTFail("duplicate overwrote cached bytes") } catch {}
        let target = directory.appendingPathComponent(entry.id + ".sealed")
        try FileManager.default.removeItem(at: target)
        try FileManager.default.createSymbolicLink(at: target, withDestinationURL: source)
        await cannotRead(cache, entry)
    }

    func testOrphanPruningPreservesOnlyReferencedEncryptedFiles() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let bytes = Data("retained".utf8), keep = record(bytes: Data("retained".utf8)), orphan = record(bytes: Data("retained".utf8))
        let source = root.appendingPathComponent("source"), directory = root.appendingPathComponent("cache")
        try bytes.write(to: source)
        let cache = AIAttachmentCache(scope: "A", directory: directory, key: key)
        _ = try await cache.stage(source, id: keep.id, expectedSize: keep.size)
        _ = try await cache.stage(source, id: orphan.id, expectedSize: orphan.size)
        try await cache.prune(retaining: [keep.id])
        XCTAssertFalse(FileManager.default.fileExists(atPath: directory.appendingPathComponent(orphan.id + ".sealed").path))
        let actual = try await cache.read(keep); XCTAssertEqual(actual, bytes)
    }

    func testRecoveryRequiresExplicitRetryAndCannotCrossComputerHostOrControlVersion() throws {
        let original = record(bytes: Data("fixture".utf8), status: "uploading")
        let recovered = try XCTUnwrap(AIStagedAttachment.recover([original.sessionID: [original]])[original.sessionID]?.first)
        XCTAssertEqual(recovered.id, original.id); XCTAssertEqual(recovered.digest, original.digest)
        XCTAssertEqual(recovered.status, "restored"); XCTAssertNotEqual(recovered.status, "ready")
        func session(pc: UUID? = nil, id: String? = nil, version: Double = 3, control: Bool = true) -> AIConversation {
            .init(raw: .object(["id": .string(id ?? original.sessionID), "canControl": .bool(control), "controlVersion": .number(version),
                "capabilities": .object(["attachments": .bool(true)])]), pcID: pc ?? original.pcID)
        }
        XCTAssertTrue(recovered.isBound(to: session(), host: original.host))
        XCTAssertFalse(recovered.isBound(to: session(pc: UUID()), host: original.host))
        XCTAssertFalse(recovered.isBound(to: session(id: UUID().uuidString), host: original.host))
        XCTAssertFalse(recovered.isBound(to: session(version: 4), host: original.host))
        XCTAssertFalse(recovered.isBound(to: session(control: false), host: original.host))
        XCTAssertFalse(recovered.isBound(to: session(), host: UUID().uuidString))
        let ready = record(bytes: Data("ready".utf8))
        XCTAssertEqual(AIStagedAttachment.recover([ready.sessionID: [ready]])[ready.sessionID]?.first?.status, "restored")
    }

    func testRecoveryDropsDuplicateWrongSessionAndInvalidMetadata() {
        let first = record(bytes: Data("fixture".utf8))
        let invalid = record(id: "../bad", bytes: Data("fixture".utf8))
        let result = AIStagedAttachment.recover([first.sessionID: [first, first, invalid], UUID().uuidString: [first]])
        XCTAssertEqual(result.values.flatMap { $0 }.count, 1)
        XCTAssertEqual(result[first.sessionID]?.first?.id, first.id)
    }

    func testPreviouslyClearedSessionsDoNotDisplaceRecoverableAttachments() {
        let entry = record(bytes: Data("retained".utf8))
        var saved: [String: [AIStagedAttachment]] = [:]
        for _ in 0..<100 { saved[UUID().uuidString] = [] }
        saved[entry.sessionID] = [entry]
        XCTAssertEqual(AIStagedAttachment.recover(saved)[entry.sessionID]?.first?.id, entry.id)
    }

    func testFileNamesRespectTheHostUTF16LimitWithoutSplittingUnicode() throws {
        let name = try AIStagedAttachment.fileName(String(repeating: "😀", count: 100))
        XCTAssertEqual(name.utf16.count, 180)
        XCTAssertFalse(name.contains("�"))
        for invalid in ["", "C:\\file", "folder/file", "bad\0name", "line\nname"] {
            XCTAssertThrowsError(try AIStagedAttachment.fileName(invalid))
        }
    }

    func testCacheBudgetRejectsNewContentWithoutReadingOrOverwritingExistingFiles() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("cache")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let existing = directory.appendingPathComponent("budget-fixture")
        XCTAssertTrue(FileManager.default.createFile(atPath: existing.path, contents: nil))
        let file = try FileHandle(forWritingTo: existing)
        try file.truncate(atOffset: UInt64(AIAttachmentCache.maxCacheBytes)); try file.close()
        let source = root.appendingPathComponent("source"); try Data([1]).write(to: source)
        let cache = AIAttachmentCache(scope: "A", directory: directory, key: key)
        let id = UUID().uuidString.lowercased()
        do { _ = try await cache.stage(source, id: id, expectedSize: 1); XCTFail("cache budget was exceeded") } catch {}
        XCTAssertFalse(FileManager.default.fileExists(atPath: directory.appendingPathComponent(id + ".sealed").path))
        XCTAssertEqual(try existing.resourceValues(forKeys: [.fileSizeKey]).fileSize, AIAttachmentCache.maxCacheBytes)
    }
}
