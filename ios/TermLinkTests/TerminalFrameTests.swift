// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

final class TerminalFrameTests: XCTestCase {
    private func frame(graphemeID: UInt64 = 1, cellFlags: UInt64 = 0, widthProfile: UInt64 = 1) -> Data {
        var bytes = [UInt8](repeating: 0, count: 1360)
        func set(_ offset: Int, _ size: Int, _ value: UInt64) { for i in 0..<size { bytes[offset+i] = UInt8((value >> (i*8)) & 255) } }
        func crc(_ range: Range<Int>) -> UInt32 {
            var value: UInt32 = .max
            for offset in range { value ^= UInt32(bytes[offset]); for _ in 0..<8 { value = (value >> 1) ^ (value & 1 == 1 ? 0x82f63b78 : 0) } }
            return ~value
        }
        bytes.replaceSubrange(0..<8, with: Array("TLRFRM4\0".utf8))
        set(8, 2, 4); set(12, 2, 96); set(14, 2, 24); set(16, 2, 6); set(20, 4, 1360)
        set(24, 8, 1); set(32, 8, 1); set(40, 4, 1); set(44, 4, 1); set(56, 4, 1)
        set(60, 4, widthProfile); set(64, 4, 96); set(72, 8, 0x7ff)
        let sections: [(Int, Int, Int, Int)] = [(240, 9, 1, 8), (256, 16, 1, 16), (272, 1056, 264, 4), (1328, 0, 0, 16), (1328, 28, 1, 0), (1360, 0, 0, 0)]
        for (index, section) in sections.enumerated() {
            let offset = 96 + index*24
            set(offset, 4, UInt64(index+1)); set(offset+4, 2, index == 1 ? 2 : 1); set(offset+6, 2, 1)
            set(offset+8, 4, UInt64(section.0)); set(offset+12, 4, UInt64(section.1)); set(offset+16, 4, UInt64(section.2)); set(offset+20, 4, UInt64(section.3))
        }
        set(240, 4, 8); set(244, 2, 1); set(246, 1, 1); bytes[248] = 88
        for index in 0..<264 { set(272+index*4, 4, UInt64(UInt32.max)) }
        set(1328, 4, 8); set(1332, 4, 12); set(1336, 4, 1); set(1344, 4, graphemeID); set(1352, 4, cellFlags)
        set(88, 4, UInt64(crc(96..<bytes.count))); set(92, 4, UInt64(crc(0..<92)))
        return Data(bytes)
    }
    func testGraphemeIDsStartAtOneAndUnsetPaletteUsesDefaults() throws {
        let decoded = try TerminalFrame.decode(frame())
        XCTAssertEqual(decoded.cells.first?.text, "X")
        XCTAssertEqual(decoded.cells.first?.foreground, 0xd0d0d0)
        XCTAssertEqual(decoded.cells.first?.background, 0x080c10)
        XCTAssertEqual(decoded.nextSequence, 1)
    }
    func testMalformedIndexesAndUnsupportedWidthFailClosed() {
        XCTAssertThrowsError(try TerminalFrame.decode(frame(graphemeID: 2)))
        XCTAssertThrowsError(try TerminalFrame.decode(frame(graphemeID: 0)))
        XCTAssertThrowsError(try TerminalFrame.decode(frame(graphemeID: 1, cellFlags: 1)))
        XCTAssertThrowsError(try TerminalFrame.decode(frame(widthProfile: 2)))
        var corrupt = frame(); corrupt[248] ^= 1
        XCTAssertThrowsError(try TerminalFrame.decode(corrupt))
    }
}
