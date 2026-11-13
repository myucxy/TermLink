// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

final class TerminalSnapshotProtocolTests: XCTestCase {
    func testGeneratedHostNoneCompressionWireIsAccepted() throws {
        // protocol/protobuf/termlink_realtime.proto: field 12, NONE = 1.
        let container = try ProtoMessage(Data([0x60, 0x01]))
        XCTAssertEqual(container.uint(12), 1)
        XCTAssertNoThrow(try TerminalSnapshotProtocol.validateCompression(container.uint(12)))
    }
    func testUnspecifiedZstdAndUnknownCompressionFailClosed() {
        for value: UInt64 in [0, 2, 3, UInt64.max] {
            XCTAssertThrowsError(try TerminalSnapshotProtocol.validateCompression(value))
        }
    }
}
