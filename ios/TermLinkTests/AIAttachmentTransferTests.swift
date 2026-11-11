// SPDX-License-Identifier: GPL-3.0-only
import CryptoKit
import XCTest
@testable import TermLink

@MainActor final class AIAttachmentTransferTests: XCTestCase {
    private func record(_ bytes: Data) -> AIStagedAttachment {
        .init(id: UUID().uuidString.lowercased(), pcID: UUID(), sessionID: UUID().uuidString.lowercased(), host: UUID().uuidString.lowercased(),
              controlVersion: .number(4), name: "fixture.txt", size: Int64(bytes.count), mime: "text/plain",
              digest: Data(SHA256.hash(data: bytes)).hex, sent: 0, status: "restored")
    }
    private final class Host {
        let expected: Data
        var stored = Data(), ready = false, dropChunkReply = false, dropCommitReply = false
        var beginOffsets: [Int] = [], chunkOffsets: [Int] = [], ids: [String] = []
        var commits = 0
        init(_ bytes: Data) { expected = bytes }
        func request(_ entry: AIStagedAttachment, action: String, fields: [String: JSONValue]) throws -> JSONValue {
            ids.append(entry.id)
            switch action {
            case "attachment.begin":
                XCTAssertEqual(fields["size"], .number(Double(expected.count)))
                XCTAssertEqual(fields["sha256"], .string(Data(SHA256.hash(data: expected)).hex))
                beginOffsets.append(stored.count)
            case "attachment.chunk":
                let offset = fields["offset"]?.integer ?? -1
                XCTAssertEqual(offset, Int64(stored.count))
                let bytes = try XCTUnwrap(Data(base64Encoded: fields["data"]?.string ?? ""))
                XCTAssertFalse(bytes.isEmpty); XCTAssertLessThanOrEqual(bytes.count, 24576)
                chunkOffsets.append(Int(offset)); stored.append(bytes)
                if dropChunkReply { dropChunkReply = false; throw ClientError.unavailable("lost chunk acknowledgement") }
            case "attachment.commit":
                commits += 1; XCTAssertEqual(stored, expected); ready = true
                if dropCommitReply { dropCommitReply = false; throw ClientError.unavailable("lost commit acknowledgement") }
            default: XCTFail("unexpected request: \(action)")
            }
            return .object(["attachment": .object(["offset": .number(Double(stored.count)), "ready": .bool(ready)])])
        }
    }

    func testRetryAfterLostChunkReplyUsesHostOffsetWithoutDuplicatingBytes() async throws {
        let bytes = Data((0..<70001).map { UInt8($0 % 251) }), entry = record(Data((0..<70001).map { UInt8($0 % 251) }))
        let host = Host(bytes); host.dropChunkReply = true
        do {
            _ = try await AIAttachmentTransfer.upload(entry, bytes: bytes, chunkBytes: 24576,
                request: { action, fields in try host.request(entry, action: action, fields: fields) }, progress: { _ in })
            XCTFail("lost reply must leave the upload pending")
        } catch {}
        XCTAssertEqual(host.stored.count, 24576)
        // A new process still has its old local offset. Begin, not that offset, decides where to resume.
        let restored = try XCTUnwrap(AIStagedAttachment.recover([entry.sessionID: [entry]])[entry.sessionID]?.first)
        let result = try await AIAttachmentTransfer.upload(restored, bytes: bytes, chunkBytes: 24576,
            request: { action, fields in try host.request(restored, action: action, fields: fields) }, progress: { _ in })
        XCTAssertEqual(host.beginOffsets, [0, 24576])
        XCTAssertEqual(host.chunkOffsets, [0, 24576, 49152])
        XCTAssertEqual(Set(host.ids), [entry.id])
        XCTAssertEqual(host.stored, bytes); XCTAssertEqual(result.status, "ready"); XCTAssertEqual(result.sent, entry.size)
    }

    func testRetryAfterLostCommitReplyRecognizesReadyAttachmentWithoutUploadingAgain() async throws {
        let bytes = Data("committed but reply lost".utf8), entry = record(Data("committed but reply lost".utf8)), host = Host(Data("committed but reply lost".utf8))
        host.dropCommitReply = true
        do {
            _ = try await AIAttachmentTransfer.upload(entry, bytes: bytes, chunkBytes: 10,
                request: { action, fields in try host.request(entry, action: action, fields: fields) }, progress: { _ in })
            XCTFail("lost commit reply accepted")
        } catch {}
        let chunks = host.chunkOffsets.count
        let result = try await AIAttachmentTransfer.upload(entry, bytes: bytes, chunkBytes: 10,
            request: { action, fields in try host.request(entry, action: action, fields: fields) }, progress: { _ in })
        XCTAssertEqual(host.commits, 1); XCTAssertEqual(host.chunkOffsets.count, chunks)
        XCTAssertEqual(result.status, "ready"); XCTAssertEqual(host.beginOffsets, [0, bytes.count])
    }

    func testInvalidOffsetsAndReadyStateAreRejectedBeforeAnyChunk() async {
        let bytes = Data("fixture".utf8), entry = record(Data("fixture".utf8))
        for invalid in [JSONValue.number(-1), .number(1.5), .number(8), .string("0"), .null] {
            var calls = 0
            do {
                _ = try await AIAttachmentTransfer.upload(entry, bytes: bytes, chunkBytes: 3, request: { action, _ in
                    calls += 1; XCTAssertEqual(action, "attachment.begin")
                    return .object(["attachment": .object(["offset": invalid, "ready": .bool(false)])])
                }, progress: { _ in })
                XCTFail("invalid offset accepted")
            } catch {}
            XCTAssertEqual(calls, 1)
        }
        for body in [JSONValue.object(["offset": .number(0), "ready": .bool(true)]), .object(["offset": .number(0), "ready": .string("true")])] {
            do {
                _ = try await AIAttachmentTransfer.upload(entry, bytes: bytes, chunkBytes: 3,
                    request: { _, _ in .object(["attachment": body]) }, progress: { _ in })
                XCTFail("invalid ready state accepted")
            } catch {}
        }
    }

    func testUnconfirmedChunkOrCommitNeverMarksAttachmentReady() async {
        let bytes = Data("fixture".utf8), entry = record(Data("fixture".utf8))
        for badChunk in [true, false] {
            var ready = false
            do {
                _ = try await AIAttachmentTransfer.upload(entry, bytes: bytes, chunkBytes: 24576, request: { action, _ in
                    let offset = action == "attachment.begin" || badChunk ? 0 : bytes.count
                    return .object(["attachment": .object(["offset": .number(Double(offset)), "ready": .bool(false)])])
                }, progress: { ready = ready || $0.status == "ready" })
                XCTFail("unconfirmed upload accepted")
            } catch {}
            XCTAssertFalse(ready)
        }
    }

    func testChangedCacheContentAndInvalidChunkBudgetNeverReachTheHost() async {
        let bytes = Data("fixture".utf8), entry = record(Data("fixture".utf8))
        for (content, chunk) in [(Data("changed".utf8), 3), (bytes, 0), (bytes, 24577)] {
            var calls = 0
            do {
                _ = try await AIAttachmentTransfer.upload(entry, bytes: content, chunkBytes: chunk,
                    request: { _, _ in calls += 1; return .null }, progress: { _ in })
                XCTFail("invalid upload accepted")
            } catch {}
            XCTAssertEqual(calls, 0)
        }
    }
}
