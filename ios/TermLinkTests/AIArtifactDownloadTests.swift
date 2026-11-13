// SPDX-License-Identifier: GPL-3.0-only
import CryptoKit
import XCTest
@testable import TermLink

private func artifactSession(pc: UUID = UUID(), id: String = UUID().uuidString.lowercased(), directory: String = "/test/project",
                             style: String = "posix", artifacts: Bool = true) -> AIConversation {
    .init(raw: .object(["id": .string(id), "directory": .string(directory), "kind": .string("ai"),
        "capabilities": .object(["pathStyle": .string(style), "artifacts": .bool(artifacts)])]), pcID: pc)
}
private func artifactSource() throws -> AIArtifactSource {
    try .init(session: artifactSession(), hostID: UUID().uuidString.lowercased(), artifactID: "out/result.txt", version: "100:version1")
}
private func artifactPage(_ source: AIArtifactSource, data: Data, offset: Int64, size: Int64) -> JSONValue {
    .object(["id": .string(source.artifactID), "version": .string(source.version), "offset": .number(Double(offset)),
        "nextOffset": .number(Double(offset) + Double(data.count)), "size": .number(Double(size)), "data": .string(data.base64EncodedString())])
}

final class AIArtifactDownloadContractTests: XCTestCase {
    func testSourceBindsOriginalHostPCSessionDirectoryAndPathStyle() throws {
        let session = artifactSession(), host = UUID().uuidString.lowercased()
        let source = try AIArtifactSource(session: session, hostID: host, artifactID: "out/result.txt", version: "1:2")
        XCTAssertTrue(source.matches(session, host: host))
        XCTAssertFalse(source.matches(session, host: UUID().uuidString))
        XCTAssertFalse(source.matches(artifactSession(id: session.id), host: host))
        XCTAssertFalse(source.matches(artifactSession(pc: session.pcID), host: host))
        XCTAssertFalse(source.matches(artifactSession(pc: session.pcID, id: session.id, directory: "/other"), host: host))
        XCTAssertFalse(source.matches(artifactSession(pc: session.pcID, id: session.id, style: ""), host: host))
        XCTAssertFalse(source.matches(artifactSession(pc: session.pcID, id: session.id, artifacts: false), host: host))
    }
    func testArtifactPagesRequireTypedContiguousStableIdentityAndSize() throws {
        let source = try artifactSource(), page = artifactPage(source, data: Data("abc".utf8), offset: 0, size: 3)
        XCTAssertEqual(try AIArtifactChunk.decode(page, source: source, offset: 0, expectedSize: nil).nextOffset, 3)
        let bad: [String: JSONValue] = ["id": .string("other"), "version": .string("changed"), "offset": .string("0"),
            "nextOffset": .number(2), "size": .number(2), "data": .string("invalid%")]
        for (key, value) in bad { var object = page.object; object[key] = value
            XCTAssertThrowsError(try AIArtifactChunk.decode(.object(object), source: source, offset: 0, expectedSize: 3), key)
        }
        XCTAssertThrowsError(try AIArtifactChunk.decode(page, source: source, offset: 0, expectedSize: 4))
        XCTAssertThrowsError(try AIArtifactChunk.decode(artifactPage(source, data: Data(), offset: 0, size: 1), source: source, offset: 0, expectedSize: nil))
        XCTAssertNoThrow(try AIArtifactChunk.decode(artifactPage(source, data: Data(), offset: 0, size: 0), source: source, offset: 0, expectedSize: nil))
        XCTAssertThrowsError(try AIArtifactChunk.decode(artifactPage(source, data: Data(repeating: 1, count: 65537), offset: 0, size: 65537), source: source, offset: 0, expectedSize: nil))
    }
    func testUnknownPathsAndMissingArtifactCapabilityDoNotCreateJobs() throws {
        XCTAssertThrowsError(try AIArtifactSource(session: artifactSession(style: "unknown"), hostID: UUID().uuidString, artifactID: "file", version: "1"))
        XCTAssertThrowsError(try AIArtifactSource(session: artifactSession(artifacts: false), hostID: UUID().uuidString, artifactID: "file", version: "1"))
        XCTAssertThrowsError(try AIArtifactSource(session: artifactSession(), hostID: UUID().uuidString, artifactID: "../file", version: "1"))
        let session = artifactSession(directory: "/tmp/work")
        let source = try AIArtifactSource(session: session, hostID: UUID().uuidString, artifactID: "out/keep\\slash:name.txt", version: "1")
        XCTAssertEqual(source.filename, "keep\\slash:name.txt")
    }
}

final class AIArtifactDownloadCacheTests: XCTestCase {
    private var directory: URL!
    private let key = SymmetricKey(data: Data(repeating: 31, count: 32))
    override func setUpWithError() throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
    }
    override func tearDownWithError() throws { if let directory { try? FileManager.default.removeItem(at: directory) } }
    private func cache(scope: String = "account-fixture") -> AIArtifactDownloadCache { .init(scope: scope, directory: directory, key: key) }
    func testEncryptedPartialRestoresWithoutAutomaticNetworkAndExportsExactBytes() async throws {
        let source = try artifactSource(), store = cache(), bytes = Data((0..<100_000).map { UInt8($0 % 251) })
        let record = try await store.insert(source: source)
        _ = try await store.resume(record.id)
        let first = bytes.prefix(65536)
        _ = try await store.append(record.id, chunk: .decode(artifactPage(source, data: first, offset: 0, size: Int64(bytes.count)), source: source, offset: 0, expectedSize: nil))
        let restored = cache(), items = try await restored.restore(), saved = try XCTUnwrap(items.first)
        XCTAssertEqual(saved.status, .restored); XCTAssertEqual(saved.received, 65536)
        XCTAssertThrowsError(try String(contentsOf: directory.appendingPathComponent("metadata.sealed"), encoding: .utf8))
        let resumed = try await restored.resume(record.id)
        _ = try await restored.append(record.id, chunk: .decode(artifactPage(source, data: bytes.suffix(bytes.count - 65536), offset: resumed.received, size: Int64(bytes.count)), source: source, offset: resumed.received, expectedSize: resumed.expectedBytes))
        let export = try await restored.export(record.id)
        XCTAssertEqual(try Data(contentsOf: export), bytes)
        try await restored.discardExport(export)
        XCTAssertFalse(FileManager.default.fileExists(atPath: export.path))
        let sealed = try Data(contentsOf: directory.appendingPathComponent(record.id.uuidString.lowercased() + ".part"))
        XCTAssertNil(sealed.range(of: bytes.prefix(256)))
    }
    func testCrashTailIsDiscardedButCommittedFrameTamperingFailsClosed() async throws {
        let source = try artifactSource(), store = cache(), record = try await store.insert(source: source)
        _ = try await store.resume(record.id)
        _ = try await store.append(record.id, chunk: .decode(artifactPage(source, data: Data("committed".utf8), offset: 0, size: 100), source: source, offset: 0, expectedSize: nil))
        let path = directory.appendingPathComponent(record.id.uuidString.lowercased() + ".part")
        let handle = try FileHandle(forWritingTo: path); try handle.seekToEnd(); try handle.write(contentsOf: Data("uncommitted tail".utf8)); try handle.close()
        let next = cache(); _ = try await next.restore(); let resumed = try await next.resume(record.id)
        XCTAssertEqual(try Data(contentsOf: path).count, Int(resumed.sealedBytes))
        var corrupt = try Data(contentsOf: path); corrupt[6] ^= 1; try corrupt.write(to: path)
        _ = try await next.stop(record.id, status: .paused)
        do { _ = try await next.resume(record.id); XCTFail("Tampering must not resume") } catch {}
    }
    func testOtherAccountCannotReadMetadataAndSymlinkPartCannotBeOpened() async throws {
        let store = cache(), record = try await store.insert(source: artifactSource())
        do { _ = try await cache(scope: "another-account").restore(); XCTFail("Cross-account metadata must fail") } catch {}
        let path = directory.appendingPathComponent(record.id.uuidString.lowercased() + ".part")
        try FileManager.default.removeItem(at: path)
        let outside = directory.deletingLastPathComponent().appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: outside) }
        try Data("outside".utf8).write(to: outside)
        try FileManager.default.createSymbolicLink(at: path, withDestinationURL: outside)
        do { _ = try await store.resume(record.id); XCTFail("Symlink must be rejected") } catch {}
        XCTAssertEqual(try Data(contentsOf: outside), Data("outside".utf8))
    }
    func testEncryptedFramesStayBoundedAndMissingFileRemovalIsSafe() async throws {
        let store = cache(), source = try artifactSource(), record = try await store.insert(source: source)
        _ = try await store.resume(record.id)
        let data = Data(repeating: 55, count: 65536)
        _ = try await store.append(record.id, chunk: .decode(artifactPage(source, data: data, offset: 0, size: 65536), source: source, offset: 0, expectedSize: nil))
        let path = directory.appendingPathComponent(record.id.uuidString.lowercased() + ".part")
        let sealed = try Data(contentsOf: path)
        var at = 0, frames = 0
        while at < sealed.count {
            let length = sealed[at..<(at + 4)].reduce(UInt32(0)) { ($0 << 8) | UInt32($1) }
            XCTAssertLessThanOrEqual(length, 16384); at += 4 + Int(length) + 28; frames += 1
        }
        XCTAssertEqual(frames, 4); XCTAssertEqual(at, sealed.count)
        try FileManager.default.removeItem(at: path)
        try await store.remove(record.id)
        let saved = try await cache().restore(); XCTAssertTrue(saved.isEmpty)
    }
    func testRestorePrunesOnlyOwnUncommittedRandomFiles() async throws {
        let store = cache(), record = try await store.insert(source: artifactSource())
        let orphan = directory.appendingPathComponent(UUID().uuidString.lowercased() + ".part")
        let temporary = directory.appendingPathComponent(UUID().uuidString.lowercased() + ".meta-tmp")
        try Data("uncommitted".utf8).write(to: orphan); try Data("metadata draft".utf8).write(to: temporary)
        let next = cache(), restored = try await next.restore()
        XCTAssertEqual(restored.map(\.id), [record.id]); XCTAssertFalse(FileManager.default.fileExists(atPath: orphan.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: temporary.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: directory.appendingPathComponent(record.id.uuidString.lowercased() + ".part").path))
    }
    func testMetadataAndFrameOwnershipCannotBeRebound() async throws {
        let store = cache(), source = try artifactSource(), a = try await store.insert(source: source), b = try await store.insert(source: source)
        _ = try await store.resume(a.id); _ = try await store.resume(b.id)
        let chunk = try AIArtifactChunk.decode(artifactPage(source, data: Data("same source".utf8), offset: 0, size: 20), source: source, offset: 0, expectedSize: nil)
        _ = try await store.append(a.id, chunk: chunk); _ = try await store.append(b.id, chunk: chunk)
        let first = directory.appendingPathComponent(a.id.uuidString.lowercased() + ".part"), second = directory.appendingPathComponent(b.id.uuidString.lowercased() + ".part")
        try Data(contentsOf: first).write(to: second)
        _ = try await store.stop(b.id, status: .paused)
        do { _ = try await store.resume(b.id); XCTFail("A frame is bound to its random download ID") } catch {}
        var metadata = try Data(contentsOf: directory.appendingPathComponent("metadata.sealed")); metadata[0] ^= 1
        try metadata.write(to: directory.appendingPathComponent("metadata.sealed"))
        do { _ = try await cache().restore(); XCTFail("Tampered metadata must not become download tasks") } catch {}
    }
    func testEmptyArtifactAndCancelRemoveKeepDistinctState() async throws {
        let source = try artifactSource(), store = cache(), record = try await store.insert(source: source)
        _ = try await store.resume(record.id)
        let done = try await store.append(record.id, chunk: .decode(artifactPage(source, data: Data(), offset: 0, size: 0), source: source, offset: 0, expectedSize: nil))
        XCTAssertEqual(done.status, .completed)
        let file = try await store.export(record.id); XCTAssertTrue(try Data(contentsOf: file).isEmpty); try await store.discardExport(file)
        let second = try await store.insert(source: source)
        let cancelled = try await store.stop(second.id, status: .cancelled); XCTAssertEqual(cancelled.status, .cancelled)
        try await store.remove(second.id)
        let restored = try await cache().restore(); XCTAssertEqual(restored.map(\.id), [record.id])
    }
}

@MainActor final class AIArtifactDownloadStoreTests: XCTestCase {
    private var directory: URL!
    override func setUpWithError() throws { directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true) }
    override func tearDownWithError() throws { if let directory { try? FileManager.default.removeItem(at: directory) } }
    private func cache() -> AIArtifactDownloadCache { .init(scope: "store-test", directory: directory, key: SymmetricKey(data: Data(repeating: 3, count: 32))) }
    private func wait(_ condition: () -> Bool) async throws {
        for _ in 0..<200 { if condition() { return }; try await Task.sleep(for: .milliseconds(10)) }
        XCTFail("Timed out waiting for download state")
    }
    func testPauseThenExplicitRetryResumesSameSourceAndCommittedOffset() async throws {
        let source = try artifactSource(), bytes = Data(repeating: 42, count: 80_000), store = AIArtifactDownloadStore()
        var confirms: [AIArtifactSource] = [], offsets: [Int64] = [], holdSecond = true
        try await store.activate(cache: cache(), confirm: { confirms.append($0) }, read: { requested, offset in
            XCTAssertEqual(requested, source); offsets.append(offset)
            if offset > 0, holdSecond { try await Task.sleep(for: .seconds(30)) }
            let count = min(65536, bytes.count - Int(offset))
            return artifactPage(requested, data: bytes.subdata(in: Int(offset)..<(Int(offset) + count)), offset: offset, size: Int64(bytes.count))
        })
        let id = try await store.start(source: source)
        try await wait { store.records.first?.received == 65536 }
        await store.pause(id)
        XCTAssertEqual(store.records.first?.status, .paused)
        holdSecond = false; store.retry(id)
        try await wait { store.records.first?.status == .completed && !store.isBusy(id) }
        XCTAssertEqual(confirms, [source, source]); XCTAssertEqual(offsets.filter { $0 == 0 }.count, 1)
        await store.share(id); let url = try XCTUnwrap(store.exportURL); XCTAssertEqual(try Data(contentsOf: url), bytes)
        store.dismissShare(); store.retire()
    }
    func testContextFailureAndVersionMutationNeverAppendAndRequireExplicitRetry() async throws {
        let source = try artifactSource(), store = AIArtifactDownloadStore()
        var reads = 0
        try await store.activate(cache: cache(), confirm: { _ in throw ClientError.readOnly }, read: { _, _ in reads += 1; return .null })
        let id = try await store.start(source: source)
        try await wait { store.records.first?.status == .failed && !store.isBusy(id) }
        XCTAssertEqual(reads, 0); XCTAssertEqual(store.records.first?.received, 0)
        let cache = cache(); store.retire()
        try await store.activate(cache: cache, confirm: { _ in }, read: { source, offset in
            reads += 1; var value = artifactPage(source, data: Data("x".utf8), offset: offset, size: 1).object; value["version"] = .string("different"); return .object(value)
        })
        XCTAssertEqual(reads, 0, "Restoring a task must never start network access")
        store.retry(id); try await wait { store.records.first?.status == .failed && !store.isBusy(id) }
        XCTAssertEqual(reads, 1); XCTAssertEqual(store.records.first?.received, 0)
    }
    func testReactivationWaitsForOldWriterAndRestoreDoesNotAutoRetry() async throws {
        let source = try artifactSource(), store = AIArtifactDownloadStore()
        var reads = 0
        try await store.activate(cache: cache(), confirm: { _ in }, read: { _, _ in reads += 1; try await Task.sleep(for: .seconds(30)); return .null })
        _ = try await store.start(source: source)
        try await wait { reads == 1 }
        store.retire()
        try await store.activate(cache: cache(), confirm: { _ in }, read: { _, _ in reads += 1; return .null })
        XCTAssertEqual(reads, 1)
        XCTAssertEqual(store.records.count, 1); XCTAssertEqual(store.records.first?.status, .paused)
        XCTAssertFalse(store.isBusy(store.records[0].id))
        store.retire()
    }
    func testCancelAndRetireCannotPublishIntoAnotherAccount() async throws {
        let source = try artifactSource(), store = AIArtifactDownloadStore()
        try await store.activate(cache: cache(), confirm: { _ in }, read: { _, _ in try await Task.sleep(for: .seconds(30)); return .null })
        let id = try await store.start(source: source)
        try await wait { store.records.first?.status == .downloading }
        await store.cancel(id); XCTAssertEqual(store.records.first?.status, .cancelled)
        store.retry(id); try await wait { store.isBusy(id) }
        store.retire(); XCTAssertTrue(store.records.isEmpty); XCTAssertFalse(store.available)
        try await Task.sleep(for: .milliseconds(30)); XCTAssertTrue(store.records.isEmpty)
    }
}
