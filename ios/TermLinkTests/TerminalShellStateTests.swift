// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

final class TerminalShellStateTests: XCTestCase {
    private let directory = "file:///C:/work/%E4%B8%AD%20test"

    private func put(_ value: UInt64, at offset: Int, size: Int, in data: inout Data) {
        for index in 0..<size { data[offset + index] = UInt8((value >> (index * 8)) & 255) }
    }
    private func point(sequence: UInt64 = 4, line: UInt64 = 0, cell: UInt32 = 0, page: UInt8 = 1, alternate: UInt8 = 0) -> Data {
        var data = Data(repeating: 0, count: 24)
        put(sequence, at: 0, size: 8, in: &data); put(line, at: 8, size: 8, in: &data)
        put(UInt64(cell), at: 16, size: 4, in: &data); data[20] = page; data[21] = alternate
        return data
    }
    private func command(id: UInt64 = 1, phase: UInt8 = 3, flags: UInt8 = 15, code: Int32 = -1,
                         points: [Data]? = nil, cwd: Data? = nil) -> Data {
        let cwd = cwd ?? Data(directory.utf8)
        var data = Data(repeating: 0, count: 16)
        put(id, at: 0, size: 8, in: &data); data[8] = phase; data[9] = flags
        put(UInt64(UInt32(bitPattern: code)), at: 12, size: 4, in: &data)
        for value in points ?? (0..<4).map({ point(cell: UInt32($0 * 2)) }) { data.append(value) }
        data.append(Data(repeating: 0, count: 4)); put(UInt64(cwd.count), at: 112, size: 4, in: &data)
        data.append(cwd)
        return data
    }
    private func state(nextID: UInt64 = 2, cwd: Data? = nil, commands: [Data]? = nil) -> Data {
        let cwd = cwd ?? Data(directory.utf8), commands = commands ?? [command()]
        var data = Data("TLSHV1\0\0".utf8)
        data.append(Data(repeating: 0, count: 24))
        put(nextID, at: 8, size: 8, in: &data); put(UInt64(cwd.count), at: 16, size: 4, in: &data)
        put(UInt64(commands.count), at: 20, size: 4, in: &data)
        data.append(cwd); commands.forEach { data.append($0) }
        return data
    }

    func testAndroidFixturePreservesCommandAndUnicodeDirectory() throws {
        let result = try TerminalShellState.decode(state(), nextSequence: 5)
        XCTAssertEqual(result.currentDirectoryURI, directory)
        XCTAssertEqual(result.nextCommandID, 2)
        let item = try XCTUnwrap(result.commands.first)
        XCTAssertEqual(item.id, 1); XCTAssertEqual(item.phase, .completed)
        XCTAssertEqual(item.exitCode, -1); XCTAssertEqual(item.currentDirectoryURI, directory)
        XCTAssertEqual(item.prompt.sourceSequence, 4); XCTAssertEqual(item.end?.cellOffset, 6)
        XCTAssertEqual(TerminalShellState.localWindowsDirectory(result.currentDirectoryURI), "C:\\work\\中 test")
    }

    func testEveryTruncatedBoundaryMagicReservedAndTrailingBytesAreRejected() {
        let fixture = state()
        for end in 0..<fixture.count { XCTAssertThrowsError(try TerminalShellState.decode(Data(fixture.prefix(end)), nextSequence: 5), "truncated at \(end)") }
        for (offset, value) in [(0, 0), (5, 50), (6, 1), (24, 1), (31, 1), (8, 0), (20, 65)] {
            var bad = fixture; bad[offset] = UInt8(value)
            XCTAssertThrowsError(try TerminalShellState.decode(bad, nextSequence: 5), "header offset \(offset)")
        }
        var trailing = fixture; trailing.append(0)
        XCTAssertThrowsError(try TerminalShellState.decode(trailing, nextSequence: 5))
        XCTAssertThrowsError(try TerminalShellState.decode(Data(repeating: 0, count: TerminalShellState.maxBytes + 1), nextSequence: 5))
    }

    func testEmptyStateAndUnsignedCoreCounterBoundaries() throws {
        XCTAssertEqual(try TerminalShellState.decode(state(nextID: 1, cwd: Data(), commands: []), nextSequence: 0), .empty)
        let highPoint = point(sequence: UInt64.max - 1, line: UInt64.max, cell: UInt32.max, page: 6)
        let highCommand = command(id: UInt64.max - 1, phase: 0, flags: 0, code: 0,
                                  points: [highPoint] + Array(repeating: Data(repeating: 0, count: 24), count: 3), cwd: Data())
        let result = try TerminalShellState.decode(state(nextID: UInt64.max, cwd: Data(), commands: [highCommand]), nextSequence: UInt64.max)
        XCTAssertEqual(result.nextCommandID, UInt64.max)
        XCTAssertEqual(result.commands[0].prompt.lineID, UInt64.max)
        XCTAssertEqual(result.commands[0].prompt.cellOffset, UInt32.max)
        XCTAssertThrowsError(try TerminalShellState.decode(state(), nextSequence: 4))
        XCTAssertThrowsError(try TerminalShellState.decode(state(), nextSequence: 0))
    }

    func testAllCorePhasesAndOptionalAnchorCombinationsDecode() throws {
        let absent = Data(repeating: 0, count: 24), anchor = point()
        let fixtures: [(UInt8, UInt8, [Data])] = [
            (0, 0, [anchor, absent, absent, absent]),
            (1, 1, [anchor, anchor, absent, absent]),
            (2, 2, [anchor, absent, anchor, absent]),
            (3, 4, [anchor, absent, absent, anchor]),
            (4, 3, [anchor, anchor, anchor, absent])
        ]
        for (phase, flags, points) in fixtures {
            let result = try TerminalShellState.decode(state(commands: [command(phase: phase, flags: flags, code: 0, points: points)]), nextSequence: 5)
            XCTAssertEqual(result.commands[0].phase.rawValue, phase)
            XCTAssertNil(result.commands[0].exitCode)
        }
    }

    func testInvalidCommandIDsPhasesFlagsExitCodesAndReservedFieldsAreRejected() {
        let absent = Data(repeating: 0, count: 24), anchor = point()
        let records = [
            command(id: 0), command(id: 2), command(phase: 5), command(flags: 31),
            command(flags: 7), // An exit code requires its flag.
            command(phase: 2, flags: 10), // Exit code requires an end point.
            command(phase: 0), command(phase: 1), command(phase: 2), // Inconsistent phase/end flag.
            command(phase: 3, flags: 0, code: 0, points: [anchor, absent, absent, absent]),
            command(phase: 2, flags: 0, code: 0, points: [anchor, absent, absent, absent]),
            command(phase: 1, flags: 1, code: 0, points: [anchor, anchor, anchor, absent]) // Nonzero absent anchor.
        ]
        for record in records { XCTAssertThrowsError(try TerminalShellState.decode(state(commands: [record]), nextSequence: 5)) }
        for relative in [10, 11] {
            var record = command(); record[relative] = 1
            XCTAssertThrowsError(try TerminalShellState.decode(state(commands: [record]), nextSequence: 5))
        }
        XCTAssertThrowsError(try TerminalShellState.decode(state(nextID: 3, commands: [command(id: 1), command(id: 1)]), nextSequence: 5))
        XCTAssertThrowsError(try TerminalShellState.decode(state(nextID: 4, commands: [command(id: 2), command(id: 1)]), nextSequence: 5))
    }

    func testAnchorSequencePageAlternateAndReservedBoundsAreRejected() {
        for badPoint in [point(sequence: 5), point(page: 0), point(page: 7), point(alternate: 2), point(page: 2, alternate: 1)] {
            XCTAssertThrowsError(try TerminalShellState.decode(state(commands: [command(points: [badPoint] + Array(repeating: point(), count: 3))]), nextSequence: 5))
        }
        for reserved in [22, 23] {
            var badPoint = point(); badPoint[reserved] = 1
            XCTAssertThrowsError(try TerminalShellState.decode(state(commands: [command(points: [badPoint] + Array(repeating: point(), count: 3))]), nextSequence: 5))
        }
        XCTAssertThrowsError(try TerminalShellState.decode(state(commands: [command(points: [point(sequence: 3), point(sequence: 2), point(sequence: 4), point(sequence: 4)])]), nextSequence: 5))
        XCTAssertThrowsError(try TerminalShellState.decode(state(nextID: 3, commands: [command(id: 1), command(id: 2, points: Array(repeating: point(sequence: 3), count: 4))]), nextSequence: 5))
        let alternate = command(points: Array(repeating: point(alternate: 1), count: 4))
        XCTAssertNotNil(try? TerminalShellState.decode(state(commands: [alternate]), nextSequence: 5))
    }

    func testDirectoryUTF8SizeAndURIValidationInBothRecordLocations() {
        let invalid = [Data([0xff]), Data("https://example.test/path".utf8), Data("file://user@host/C:/a".utf8),
                       Data("file:///C:/a%".utf8), Data("file:///C:/a%GG".utf8), Data("file:///C:/a%FF".utf8),
                       Data("file:///C:/a%00".utf8), Data("file:///C:/a\u{85}".utf8),
                       Data(("file:///C:/" + String(repeating: "x", count: 4096)).utf8)]
        for bytes in invalid {
            XCTAssertThrowsError(try TerminalShellState.decode(state(cwd: bytes), nextSequence: 5))
            XCTAssertThrowsError(try TerminalShellState.decode(state(commands: [command(cwd: bytes)]), nextSequence: 5))
        }
        let maximum = Data(("file:///C:/" + String(repeating: "x", count: 4096 - "file:///C:/".utf8.count)).utf8)
        XCTAssertNotNil(try? TerminalShellState.decode(state(cwd: maximum, commands: [command(cwd: maximum)]), nextSequence: 5))
    }

    func testMaximumCommandCountAndWholeStateSizeAreBounded() throws {
        let cwd = Data(("file:///C:/" + String(repeating: "x", count: 4096 - "file:///C:/".utf8.count)).utf8)
        let maximum = state(nextID: 65, cwd: cwd, commands: (1...64).map { command(id: UInt64($0), cwd: cwd) })
        XCTAssertEqual(maximum.count, TerminalShellState.maxBytes)
        XCTAssertEqual(try TerminalShellState.decode(maximum, nextSequence: 5).commands.count, 64)
        XCTAssertThrowsError(try TerminalShellState.decode(state(nextID: 66, cwd: Data(), commands: (1...65).map { command(id: UInt64($0), cwd: Data()) }), nextSequence: 5))
    }

    func testOnlyLocalWindowsAbsoluteDirectoriesBecomeActionable() {
        let accepted = ["file:///C:/": "C:\\", "file://LOCALHOST/d:/work": "d:\\work",
                        "file:///C:/a%20b/%E4%B8%AD": "C:\\a b\\中", "file:///C:/a%2fb": "C:\\a\\b"]
        for (uri, path) in accepted { XCTAssertEqual(TerminalShellState.localWindowsDirectory(uri), path, uri) }
        let rejected = [
            "file://remote/C:/secret", "file:////server/share", "file://localhost//server/share",
            "file://user:pass@localhost/C:/a", "file://localhost:80/C:/a", "file:///C:/a%5Cb", "file:///C:/a\\b",
            "file:///C:/a%00", "file:///C:/a%1F", "file:///C:/a%7F", "file:///C:/a%C2%85", "file:///C:/a%FF",
            "file:///C:/a%", "file:///C:/a%0", "file:///C:/a%GG", "file:///C:/a%3Ab", "file:///C:/../Windows",
            "file:///C:/%2e%2e/Windows", "file:///C:/./a", "file:///C:/a.", "file:///C:/a%20", "file:///C:/a?x=1",
            "file:///C:/a#fragment", "file:///C:/a%3F", "file:///C:/a%2A", "file:///C:/a%3C", "file:///C:/a%7C",
            "file:///C:/a%22", "file:///C:relative", "file:///C:", "file:///1:/a", "file:///中:/a", "file:///usr/local",
            "file://localhost", "FILE:///C:/a", "https://example.test/a", "", "file:///C:/a\u{0}"
        ]
        for uri in rejected { XCTAssertNil(TerminalShellState.localWindowsDirectory(uri), uri) }
        // A remote OSC URI remains display-only metadata, never an open-directory action.
        XCTAssertNotNil(try? TerminalShellState.decode(state(cwd: Data("file://remote/C:/secret".utf8)), nextSequence: 5))
    }
}
