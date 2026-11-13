// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

@MainActor final class TerminalControlAcquisitionTests: XCTestCase {
    func testAlreadyControllingDoesNotRequestAgain() async throws {
        var calls = 0
        try await TerminalControlAcquisition.acquire(current: { true }, controlling: { true }, request: { calls += 1 })
        XCTAssertEqual(calls, 0)
    }
    func testLeaseContentionUsesAndroidScheduleWithoutForceRelease() async throws {
        var calls = 0, delays: [Duration] = []
        try await TerminalControlAcquisition.acquire(current: { true }, controlling: { false }, request: {
            calls += 1
            if calls <= 6 { throw APIError(status: 409, code: "LEASE_HELD", traceID: "") }
        }, pause: { delays.append($0) })
        XCTAssertEqual(calls, 7)
        XCTAssertEqual(delays, TerminalControlAcquisition.heldDelays)
    }
    func testExhaustedHeldLeaseRemainsFailure() async {
        var calls = 0
        do {
            try await TerminalControlAcquisition.acquire(current: { true }, controlling: { false }, request: {
                calls += 1; throw APIError(status: 409, code: "LEASE_HELD", traceID: "")
            }, pause: { _ in })
            XCTFail("Occupied lease was treated as granted")
        } catch let error as APIError { XCTAssertEqual(error.code, "LEASE_HELD") }
        catch { XCTFail("Unexpected error") }
        XCTAssertEqual(calls, 7)
    }
    func testTransientScheduleAndPermissionDenialAreDistinct() async throws {
        var calls = 0, delays: [Duration] = []
        try await TerminalControlAcquisition.acquire(current: { true }, controlling: { false }, request: {
            calls += 1
            if calls < 4 { throw URLError(.networkConnectionLost) }
        }, pause: { delays.append($0) })
        XCTAssertEqual(delays, TerminalControlAcquisition.transientDelays)
        calls = 0
        do {
            try await TerminalControlAcquisition.acquire(current: { true }, controlling: { false }, request: {
                calls += 1; throw APIError(status: 403, code: "DENIED", traceID: "")
            }, pause: { _ in XCTFail("Permission denial must not retry") })
            XCTFail("Permission denial was accepted")
        } catch { }
        XCTAssertEqual(calls, 1)
    }
    func testIdentityChangeStopsWaitingWithoutAnotherRequest() async {
        var current = true, calls = 0
        do {
            try await TerminalControlAcquisition.acquire(current: { current }, controlling: { false }, request: {
                calls += 1; throw APIError(status: 409, code: "LEASE_HELD", traceID: "")
            }, pause: { _ in current = false })
            XCTFail("Stale owner continued acquiring control")
        } catch is CancellationError { }
        catch { XCTFail("Expected cancellation") }
        XCTAssertEqual(calls, 1)
    }
    func testTaskCancellationStopsRetrySchedule() async {
        let reached = expectation(description: "waiting")
        let work = Task {
            try await TerminalControlAcquisition.acquire(current: { true }, controlling: { false }, request: {
                throw APIError(status: 409, code: "LEASE_HELD", traceID: "")
            }, pause: { _ in reached.fulfill(); try await Task.sleep(for: .seconds(60)) })
        }
        await fulfillment(of: [reached], timeout: 2)
        work.cancel()
        do { try await work.value; XCTFail("Cancelled acquisition completed") }
        catch is CancellationError { }
        catch { XCTFail("Expected cancellation") }
    }
    func testLeaseGrantRequiresValidIdentityVersionAndFutureExpiry() throws {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let id = UUID(), formatter = ISO8601DateFormatter()
        let value = JSONValue.object(["lease_id": .string(id.uuidString), "version": .number(1), "expires_at": .string(formatter.string(from: now.addingTimeInterval(30)))])
        XCTAssertEqual(try TerminalLeaseGrant.decode(value, now: now).id, id)
        for invalid in [JSONValue.object(["lease_id": .string("00000000-0000-0000-0000-000000000000"), "version": .number(1), "expires_at": value["expires_at"]]),
                        .object(["lease_id": value["lease_id"], "version": .number(0), "expires_at": value["expires_at"]]),
                        .object(["lease_id": value["lease_id"], "version": .number(1), "expires_at": .string(formatter.string(from: now))])] {
            XCTAssertThrowsError(try TerminalLeaseGrant.decode(invalid, now: now))
        }
    }
    func testTabsKeepOpeningOrderAndAdjacentSelection() {
        let a = UUID(), b = UUID(), c = UUID()
        var tabs = CLISessionTabs()
        tabs.open(a); tabs.open(b); tabs.open(c); tabs.open(b)
        XCTAssertEqual(tabs.ids, [a, b, c]); XCTAssertEqual(tabs.selected, b)
        tabs.close(b)
        XCTAssertEqual(tabs.ids, [a, c]); XCTAssertEqual(tabs.selected, c)
        tabs.close(a); XCTAssertEqual(tabs.selected, c)
        tabs.close(c); XCTAssertNil(tabs.selected)
        tabs.select(a); XCTAssertNil(tabs.selected)
    }
}
