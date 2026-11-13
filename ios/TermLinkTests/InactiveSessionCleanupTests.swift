// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

@MainActor final class InactiveSessionCleanupTests: XCTestCase {
    func testResponseRequiresExactRequestAndEveryDistinctKnownResult() throws {
        let request = UUID(), ids = [UUID(), UUID()]
        let valid = response(request, ids.map { ($0, "DELETED") })
        XCTAssertEqual(try SessionCleanupReply.decode(valid, requestID: request, sessionIDs: ids).outcomes.count, 2)
        var malformed: [ProtoMessage] = [response(UUID(), ids.map { ($0, "DELETED") }), response(request, [(ids[0], "DELETED")]),
            response(request, [(ids[0], "DELETED"), (ids[0], "DELETED")]), response(request, [(ids[0], "DELETED"), (UUID(), "DELETED")]),
            response(request, [(ids[0], "DELETED"), (ids[1], "FUTURE")]), response(request, ids.map { ($0, "DELETED") }, code: "DENIED")]
        var duplicate = valid; duplicate.add(1, request.bytes); malformed.append(duplicate)
        var badItem = ProtoMessage(); badItem.set(1, ids[0].bytes); badItem.set(2, "DELETED"); badItem.add(2, Data("DELETED".utf8))
        var duplicateStatus = ProtoMessage(); duplicateStatus.set(1, request.bytes); duplicateStatus.add(2, badItem.encoded()); malformed.append(duplicateStatus)
        for value in malformed { XCTAssertThrowsError(try SessionCleanupReply.decode(value, requestID: request, sessionIDs: ids)) }
    }
    func testMixedResultNeverTreatsSkippedOrFailedAsDeletedAndRetryUsesOriginalIDs() async throws {
        let f = Fixture(count: 4); let store = f.store()
        await store.prepare(pc: f.pc)
        let ids = store.sessions.map(\.id)
        f.reply = { request, sent in self.response(request, zip(sent, ["DELETED", "NOT_FOUND", "SKIPPED_ACTIVE", "FAILED"]).map { ($0.0, $0.1) }) }
        await store.run()
        XCTAssertEqual(store.deleted, 2); XCTAssertEqual(store.skipped, 1); XCTAssertEqual(store.failed, 1)
        XCTAssertEqual(f.acknowledged, Set(ids.prefix(2)))
        f.sessions.append(f.session(UUID(), state: "EXITED"))
        f.reply = { request, sent in self.response(request, sent.map { ($0, "DELETED") }) }
        await store.run()
        XCTAssertEqual(f.sent.count, 2); XCTAssertEqual(f.sent[1], [ids[3]])
        XCTAssertEqual(store.deleted, 3); XCTAssertEqual(store.sessions.map(\.id), ids)
    }
    func testFreshCatalogProtectsNewlyRunningAndAbsentRecordsWithoutSendingThem() async {
        let f = Fixture(count: 3); let store = f.store(); await store.prepare(pc: f.pc)
        let ids = store.sessions.map(\.id)
        f.sessions[0].lifecycle = "RUNNING"; f.sessions.remove(at: 1)
        await store.run()
        XCTAssertEqual(f.sent, [[ids[2]]]); XCTAssertEqual(store.skipped, 2); XCTAssertEqual(store.deleted, 1)
        XCTAssertEqual(store.outcomes[ids[0]], .skippedActive); XCTAssertEqual(store.outcomes[ids[1]], .skippedMissing)
    }
    func testCancellationSettlesCurrentBatchThenStopsAndSingleFlightRejectsDuplicate() async {
        let f = Fixture(count: 65); let gate = Gate(); f.gate = gate
        let store = f.store(); await store.prepare(pc: f.pc)
        let task = Task { await store.run() }
        await gate.started()
        await store.run(); store.cancel(); task.cancel()
        XCTAssertTrue(store.running); XCTAssertEqual(f.sent.count, 1)
        gate.release(); await task.value
        XCTAssertEqual(f.sent[0].count, 64); XCTAssertEqual(f.sent.count, 1)
        XCTAssertEqual(store.deleted, 64); XCTAssertTrue(store.cancelRequested); XCTAssertFalse(store.running); XCTAssertTrue(store.retryable)
        f.gate = nil; await store.run()
        XCTAssertEqual(f.sent.count, 2); XCTAssertEqual(f.sent[1].count, 1); XCTAssertEqual(store.deleted, 65)
    }
    func testChangedAccountDevicePCAndEpochCannotAcknowledgeOrContinueOldWork() async {
        for kind in 0..<5 {
            let f = Fixture(count: 65); let gate = Gate(); f.gate = gate
            let store = f.store(); await store.prepare(pc: f.pc)
            let task = Task { await store.run() }; await gate.started()
            let old = f.binding
            switch kind {
            case 0: f.binding = .init(scope: "another-account", generation: old.generation, pcID: old.pcID, pcDeviceID: old.pcDeviceID, requesterID: old.requesterID, keyEpoch: old.keyEpoch)
            case 1: f.binding = .init(scope: old.scope, generation: UUID(), pcID: old.pcID, pcDeviceID: old.pcDeviceID, requesterID: old.requesterID, keyEpoch: old.keyEpoch)
            case 2: f.binding = .init(scope: old.scope, generation: old.generation, pcID: UUID(), pcDeviceID: old.pcDeviceID, requesterID: old.requesterID, keyEpoch: old.keyEpoch)
            case 3: f.binding = .init(scope: old.scope, generation: old.generation, pcID: old.pcID, pcDeviceID: UUID(), requesterID: old.requesterID, keyEpoch: old.keyEpoch)
            default: f.binding = .init(scope: old.scope, generation: old.generation, pcID: old.pcID, pcDeviceID: old.pcDeviceID, requesterID: old.requesterID, keyEpoch: old.keyEpoch + 1)
            }
            gate.release(); await task.value
            XCTAssertEqual(f.sent.count, 1); XCTAssertTrue(f.acknowledged.isEmpty); XCTAssertEqual(store.deleted, 0)
            await store.run(); XCTAssertEqual(f.sent.count, 1)
        }
    }
    func testRetirementDoesNotReleaseInflightFenceOrPublishOldReceipt() async {
        let f = Fixture(count: 1); let gate = Gate(); f.gate = gate
        let store = f.store(); await store.prepare(pc: f.pc)
        let task = Task { await store.run() }; await gate.started(); store.retire()
        XCTAssertTrue(store.running); await store.prepare(pc: f.pc); XCTAssertTrue(store.sessions.isEmpty)
        gate.release(); await task.value
        XCTAssertFalse(store.running); XCTAssertTrue(store.sessions.isEmpty); XCTAssertTrue(f.acknowledged.isEmpty)
    }
    func testMissingMalformedOrLostResponseNeverDeletesLocalRecords() async {
        for kind in 0..<3 {
            let f = Fixture(count: 2); let store = f.store(); await store.prepare(pc: f.pc)
            f.reply = { request, sent in
                if kind == 0 { throw ClientError.unavailable("request uncertain") }
                if kind == 1 { return self.response(request, [(sent[0], "DELETED")]) }
                return self.response(request, sent.map { ($0, "UNKNOWN") })
            }
            await store.run()
            XCTAssertTrue(f.acknowledged.isEmpty); XCTAssertEqual(store.failed, 2); XCTAssertEqual(store.deleted, 0)
            XCTAssertNotNil(store.error); XCTAssertTrue(store.retryable)
        }
    }
    func testMirrorPendingCountsDurableReceiptsAndStopsNextBatch() async {
        let f = Fixture(count: 65); let store = f.store(); await store.prepare(pc: f.pc)
        f.reply = { request, sent in self.response(request, sent.map { ($0, "DELETED") }, code: "MIRROR_SYNC_PENDING") }
        await store.run()
        XCTAssertEqual(store.deleted, 64); XCTAssertEqual(f.sent.count, 1); XCTAssertTrue(store.mirrorPending)
        XCTAssertTrue(store.status.contains("服务端目录仍待同步")); XCTAssertTrue(store.retryable)
    }
    func testPostReceiptCatalogFailureKeepsAcknowledgementButDoesNotClaimCompletion() async {
        let f = Fixture(count: 1); let store = f.store(); await store.prepare(pc: f.pc)
        f.failRefreshAfterSend = true; await store.run()
        XCTAssertEqual(store.deleted, 1); XCTAssertEqual(f.acknowledged.count, 1); XCTAssertTrue(store.needsRefresh); XCTAssertNotNil(store.error)
    }
    func testUnsupportedUnauthorizedAndOversizedCatalogNeverEnableDeletion() async {
        for kind in 0..<3 {
            let f = Fixture(count: kind == 2 ? 4097 : 1)
            if kind == 0 { f.allowed = false }; if kind == 1 { f.refreshDenied = true }
            let store = f.store(); await store.prepare(pc: f.pc); await store.run()
            XCTAssertFalse(store.ready); XCTAssertNotNil(store.error); XCTAssertTrue(f.sent.isEmpty)
        }
    }
    func testPermissionRequiresExactManageRecipientScopeAndValidExpiry() throws {
        let f = Fixture(count: 1), now = Date(timeIntervalSince1970: 1_800_000_000)
        var actor: [String: JSONValue] = ["device_id": .string(f.binding.requesterID.uuidString), "device_type": .string("IOS"), "permission": .string("MANAGE")]
        func grants(_ values: [JSONValue]) -> JSONValue { .object(["pc_id": .string(f.pc.uuidString), "key_epoch": .number(1), "items": .array(values)]) }
        try SessionCleanupAuthorization.validate(grants([.object(actor)]), binding: f.binding, now: now)
        for permission in ["CONTROL", "VIEW", ""] { actor["permission"] = .string(permission); XCTAssertThrowsError(try SessionCleanupAuthorization.validate(grants([.object(actor)]), binding: f.binding, now: now)) }
        actor["permission"] = .string("MANAGE")
        XCTAssertThrowsError(try SessionCleanupAuthorization.validate(grants([.object(actor), .object(actor)]), binding: f.binding, now: now))
        actor["expires_at"] = .string("2026-01-01T00:00:00Z")
        XCTAssertThrowsError(try SessionCleanupAuthorization.validate(grants([.object(actor)]), binding: f.binding, now: now))
        actor["expires_at"] = .string("2030-01-01T00:00:00.123Z")
        try SessionCleanupAuthorization.validate(grants([.object(actor)]), binding: f.binding, now: now)
        actor["expires_at"] = .number(9999999999)
        XCTAssertThrowsError(try SessionCleanupAuthorization.validate(grants([.object(actor)]), binding: f.binding, now: now))
    }
    private func response(_ request: UUID, _ rows: [(UUID, String)], code: String = "") -> ProtoMessage {
        var result = ProtoMessage(); result.set(1, request.bytes); result.set(3, code)
        for (id, status) in rows { var row = ProtoMessage(); row.set(1, id.bytes); row.set(2, status); result.add(2, row.encoded()) }
        return result
    }
    @MainActor private final class Gate {
        var waiting = false, released = false
        var continuation: CheckedContinuation<Void, Never>?
        func hold() async { waiting = true; if !released { await withCheckedContinuation { continuation = $0 } } }
        func started() async { while !waiting { await Task.yield() } }
        func release() { released = true; continuation?.resume(); continuation = nil }
    }
    @MainActor private final class Fixture {
        let pc = UUID()
        var binding: SessionCleanupBinding
        var sessions: [TerminalSession] = []
        var sent: [[UUID]] = [], acknowledged = Set<UUID>()
        var allowed = true, refreshDenied = false, failRefreshAfterSend = false
        var gate: Gate?
        var reply: ((UUID, [UUID]) throws -> ProtoMessage)?
        init(count: Int) {
            binding = .init(scope: "fixture-account", generation: UUID(), pcID: pc, pcDeviceID: UUID(), requesterID: UUID(), keyEpoch: 1)
            sessions = (0..<count).map { session(UUID(), state: "EXITED", title: "记录 \($0 + 1)") }
        }
        func session(_ id: UUID, state: String, title: String = "Shell") -> TerminalSession {
            .init(id: id, pcID: pc, title: title, shell: "zsh", lifecycle: state, columns: 100, rows: 30, lastSequence: 1)
        }
        func store() -> InactiveSessionCleanup {
            .init(context: { [unowned self] id in guard allowed, id == binding.pcID else { throw ClientError.readOnly }; return binding },
                  refresh: { [unowned self] id in
                    guard !refreshDenied, !(failRefreshAfterSend && !sent.isEmpty) else { throw ClientError.readOnly }
                    return .init(binding: binding, sessions: sessions)
                  }, send: { [unowned self] _, ids in
                    sent.append(ids); await gate?.hold(); let request = UUID()
                    if let reply { return (request, try reply(request, ids)) }
                    var result = ProtoMessage(); result.set(1, request.bytes)
                    for id in ids { var row = ProtoMessage(); row.set(1, id.bytes); row.set(2, "DELETED"); result.add(2, row.encoded()) }
                    return (request, result)
                  }, acknowledged: { [unowned self] _, ids in acknowledged.formUnion(ids) })
        }
    }
}
