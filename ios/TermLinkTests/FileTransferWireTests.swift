// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

final class FileTransferWireTests: XCTestCase {
    func testZeroResumePreservesOneofPresenceForGeneratedHostParser() throws {
        var resume = ProtoMessage(); resume.set(1, UInt64(0))
        let bytes = try FileTransferWire.payload(type: 2, body: resume).encoded()
        XCTAssertEqual(bytes, Data([0x12, 0x00]), "A present zero-length resume message selects Resume in generated proto3 oneof parsers.")
        let decoded = try ProtoMessage(bytes)
        XCTAssertEqual(decoded.values(2).count, 1)
        XCTAssertEqual(decoded.values(2).first?.wire, 2)
        XCTAssertEqual(try ProtoMessage(decoded.bytes(2)).uint(1), 0)
    }
    func testNonzeroResumeAndAckRetainExactContiguousOffsets() throws {
        for type: UInt64 in [2, 4] {
            var message = ProtoMessage(); message.set(1, UInt64(262144))
            let decoded = try ProtoMessage(FileTransferWire.payload(type: type, body: message).encoded())
            XCTAssertEqual(decoded.fields.count, 1)
            XCTAssertEqual(try ProtoMessage(decoded.bytes(Int(type))).uint(1), 262144)
        }
    }
    func testEveryEmptyNestedMessageRetainsPresenceAndUnknownTypeIsRejected() throws {
        for type: UInt64 in 1...8 {
            let message = try ProtoMessage(FileTransferWire.payload(type: type, body: ProtoMessage()).encoded())
            XCTAssertEqual(message.values(Int(type)).count, 1)
            XCTAssertTrue(message.bytes(Int(type)).isEmpty)
        }
        for type: UInt64 in [0, 9, UInt64.max] {
            XCTAssertThrowsError(try FileTransferWire.payload(type: type, body: ProtoMessage()))
        }
    }
}
