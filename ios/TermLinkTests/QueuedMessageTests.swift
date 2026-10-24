// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

@MainActor final class QueuedMessageTests: XCTestCase {
    private func session(state: String = "idle", version: Double = 2, pending: [JSONValue] = []) -> AIConversation {
        .init(raw: .object(["id": .string(UUID().uuidString), "canControl": .bool(true), "state": .string(state), "controlVersion": .number(version), "pending": .array(pending)]), pcID: UUID())
    }
    func testQueueCannotCrossHostLeaseBusyOrRestoredState() {
        let host = JSONValue.string("host-1")
        var item = AIWorkspace.Queued(id: "queue-1", text: "hi", host: host, controlVersion: .number(2))
        XCTAssertTrue(item.canSend(session(), host: host))
        XCTAssertFalse(item.canSend(session(), host: .string("host-2")))
        XCTAssertFalse(item.canSend(session(version: 3), host: host))
        XCTAssertFalse(item.canSend(session(state: "running"), host: host))
        XCTAssertFalse(item.canSend(session(pending: [.object(["id": .string("approval")])]), host: host))
        item.restored = true; XCTAssertFalse(item.canSend(session(), host: host))
        item.restored = false; item.failed = true; XCTAssertFalse(item.canSend(session(), host: host))
    }
}
