// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

@MainActor final class TerminalSessionControlTests: XCTestCase {
    private func attachResult(state: String = "terminal_state_2_21_v2", error: String = "") -> ProtoMessage {
        var p = ProtoMessage(); p.set(2, state); p.set(3, error); return p
    }
    private func terminateResult(accepted: Bool = true) -> ProtoMessage {
        var p = ProtoMessage(); p.set(2, UInt64(accepted ? 1 : 0)); p.set(3, accepted ? "" : "MANAGE_NOT_GRANTED"); return p
    }
    func testSubscriptionUsesFreshZeroResumeAndRealSubscriberKey() throws {
        let id = UUID(), key = Data(repeating: 7, count: 32)
        let payload = try TerminalSessionControl.subscription(requestID: id, exchangeKey: key)
        XCTAssertEqual(payload.uint(1), 0)
        XCTAssertEqual(payload.bytes(2), key)
        XCTAssertEqual(payload.bytes(4), id.bytes)
        XCTAssertThrowsError(try TerminalSessionControl.subscription(requestID: id, exchangeKey: Data()))
    }
    func testTerminateWaitsForAuthorizationAndConfirmedAttachThenUnsubscribes() async throws {
        var events: [String] = []
        try await TerminalSessionControl.terminateUnopened(
            reset: { events.append("reset") },
            authorize: { events.append("authorize") },
            attach: { events.append("attach"); return self.attachResult() },
            terminate: { events.append("terminate"); return self.terminateResult() },
            unsubscribe: { events.append("unsubscribe") })
        XCTAssertEqual(events, ["reset", "authorize", "attach", "terminate", "unsubscribe"])
    }
    func testRejectedAuthorizationNeverAttachesOrTerminates() async {
        var events: [String] = []
        do {
            try await TerminalSessionControl.terminateUnopened(
                reset: {},
                authorize: { throw ClientError.authentication },
                attach: { events.append("attach"); return self.attachResult() },
                terminate: { events.append("terminate"); return self.terminateResult() },
                unsubscribe: { events.append("unsubscribe") })
            XCTFail("Authorization must fail closed")
        } catch { XCTAssertTrue(events.isEmpty) }
    }
    func testRejectedOrUnsupportedAttachNeverTerminatesAndAlwaysUnsubscribes() async {
        for response in [attachResult(error: "TERMINAL_STATE_UNSUPPORTED"), attachResult(state: "future_state")] {
            var events: [String] = []
            do {
                try await TerminalSessionControl.terminateUnopened(reset: {}, authorize: {}, attach: { response },
                    terminate: { events.append("terminate"); return self.terminateResult() },
                    unsubscribe: { events.append("unsubscribe") })
                XCTFail("Unconfirmed attach must fail closed")
            } catch { XCTAssertEqual(events, ["unsubscribe"]) }
        }
    }
    func testAttachTimeoutAndRejectedTerminationBothUnsubscribe() async {
        for failAttach in [true, false] {
            var unsubscribed = false
            do {
                try await TerminalSessionControl.terminateUnopened(reset: {}, authorize: {},
                    attach: { if failAttach { throw ClientError.cancelled }; return self.attachResult() },
                    terminate: { self.terminateResult(accepted: false) },
                    unsubscribe: { unsubscribed = true })
                XCTFail("Unconfirmed termination must not succeed")
            } catch { XCTAssertTrue(unsubscribed) }
        }
    }
}
