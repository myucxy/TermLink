// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

final class NativeCoreTests: XCTestCase {
    func testNativeCoreAndThirdPartyLicenseNoticesAreBundled() throws {
        for name in ["terminal-core-LICENSE", "terminal-core-NOTICE", "terminal-core-THIRD-PARTY-NOTICES"] {
            let url = try XCTUnwrap(Bundle.main.url(forResource: name, withExtension: "txt"))
            XCTAssertFalse(try String(contentsOf: url, encoding: .utf8).isEmpty)
        }
    }
    private func terminal(columns: UInt32 = 40, rows: UInt32 = 6) throws -> TLTerminalBridge {
        XCTAssertTrue(TLTerminalBridge.available, "The imported framework must actually be linked.")
        return try XCTUnwrap(TLTerminalBridge(columns: columns, rows: rows, sequence: 1))
    }
    private func output(_ text: String, sequence: UInt64, core: TLTerminalBridge) throws {
        try core.applyData(Data(text.utf8), kind: 1, sequence: sequence, columns: 0, rows: 0)
    }
    private func frame(_ core: TLTerminalBridge) throws -> TerminalFrame {
        try TerminalFrame.decode(XCTUnwrap(core.renderFrame()))
    }

    func testImportedCoreParsesUnicodeEncodesInputAndRestoresSnapshot() throws {
        let core = try terminal()
        try output("\u{1b}[31mTermLink iOS\u{1b}[0m\r\n中文", sequence: 1, core: core)
        let rendered = try frame(core)
        XCTAssertEqual(rendered.nextSequence, 2)
        XCTAssertTrue(rendered.cells.map(\.text).joined().contains("TermLink iOS"))
        XCTAssertTrue(rendered.cells.map(\.text).joined().contains("中文"))
        XCTAssertEqual(try core.encodeKey(1, scalar: 99, modifiers: 4, paste: nil), Data([3]))
        let snapshot = try XCTUnwrap(core.exportSnapshot())
        let restored = try terminal()
        try restored.importSnapshot(snapshot)
        XCTAssertEqual(try frame(restored).cells.map(\.text), rendered.cells.map(\.text))
        XCTAssertEqual(try frame(restored).nextSequence, rendered.nextSequence)
        XCTAssertThrowsError(try restored.importSnapshot(Data("corrupt".utf8)))
        XCTAssertEqual(try frame(restored).nextSequence, 2, "A rejected import must preserve the existing terminal.")
    }

    func testHistoryCaptureStaysImmutableWhileLiveStateAdvances() throws {
        let core = try terminal()
        try output((0..<30).map { "line-\($0)\r\n" }.joined(), sequence: 1, core: core)
        var presentation = TerminalPresentation()
        presentation.updateLive(try frame(core))
        try presentation.begin(core: core)
        XCTAssertTrue(presentation.frozen)
        XCTAssertGreaterThan(presentation.availableRows, 0)
        try presentation.scroll(core: core, by: Int.max)
        XCTAssertEqual(presentation.offset, presentation.availableRows)
        let frozen = try XCTUnwrap(presentation.frame)
        XCTAssertTrue(frozen.cells.map(\.text).joined().contains("line-0"))
        try output("new live output", sequence: 2, core: core)
        presentation.updateLive(try frame(core))
        XCTAssertEqual(presentation.frame?.nextSequence, 2)
        XCTAssertEqual(presentation.liveFrame?.nextSequence, 3)
        try presentation.scroll(core: core, by: 0)
        XCTAssertEqual(presentation.frame?.cells.map(\.text), frozen.cells.map(\.text))
        try presentation.scroll(core: core, by: Int.min)
        XCTAssertEqual(presentation.offset, 0)
        XCTAssertEqual(presentation.frame?.nextSequence, 2, "Captured tail still belongs to the frozen revision.")
        presentation.end(core: core)
        XCTAssertFalse(presentation.frozen)
        XCTAssertEqual(presentation.frame?.nextSequence, 3)
        XCTAssertTrue(presentation.frame?.cells.map(\.text).joined().contains("new live output") == true)
        XCTAssertThrowsError(try core.renderHistoryFrame(atOffset: 0))
    }

    func testSnapshotImportAndResetReleaseHistoryCaptures() throws {
        let core = try terminal()
        try output((0..<20).map { "\($0)\r\n" }.joined(), sequence: 1, core: core)
        let snapshot = try XCTUnwrap(core.exportSnapshot())
        for _ in 0..<80 {
            XCTAssertNotNil(try core.beginHistory(), "Old captures must be released before allocating the next capture.")
        }
        try core.importSnapshot(snapshot)
        XCTAssertEqual(core.historyAvailableRows, 0)
        XCTAssertThrowsError(try core.renderHistoryFrame(atOffset: 1))
        var presentation = TerminalPresentation()
        presentation.updateLive(try frame(core))
        try presentation.begin(core: core)
        presentation.reset(core: core)
        XCTAssertNil(presentation.frame)
        XCTAssertNil(presentation.liveFrame)
        XCTAssertFalse(presentation.frozen)
    }

    func testShellDirectoryCommandsAndPromptAnchorComeFromNativeCore() throws {
        let core = try terminal()
        let stream = "\u{1b}]7;file:///C:/work/%E4%B8%AD%20test\u{7}" +
            "\u{1b}]133;A\u{7}> \u{1b}]133;B\u{7}echo hello\r\n" +
            "\u{1b}]133;C\u{7}hello\r\n\u{1b}]133;D;0\u{7}"
        try output(stream, sequence: 1, core: core)
        let shell = try TerminalShellState.decode(XCTUnwrap(core.shellState()), nextSequence: 2)
        XCTAssertEqual(TerminalShellState.localWindowsDirectory(shell.currentDirectoryURI), "C:\\work\\中 test")
        let command = try XCTUnwrap(shell.commands.last)
        XCTAssertEqual(command.phase, .completed)
        XCTAssertEqual(command.exitCode, 0)
        XCTAssertNotNil(try core.beginHistory())
        XCTAssertNotNil(try core.historyOffset(forLine: command.prompt.lineID, cellOffset: command.prompt.cellOffset))
        core.endHistory()
    }

    func testCachedShellCommandCannotLocateRowsReusedAfterHardReset() throws {
        let core = try terminal()
        let prompt = "\u{1b}]133;A\u{7}> \u{1b}]133;B\u{7}echo hello\r\n\u{1b}]133;C\u{7}hello"
        try output(prompt, sequence: 1, core: core)
        let previous = try TerminalShellState.decode(core.shellState(), nextSequence: 2)
        let cached = try XCTUnwrap(previous.commands.first)
        XCTAssertNotNil(previous.command(matching: cached))
        try output("\u{1b}c" + prompt, sequence: 2, core: core)
        let current = try TerminalShellState.decode(core.shellState(), nextSequence: 3)
        let replacement = try XCTUnwrap(current.commands.first)
        XCTAssertNotEqual(replacement.id, cached.id, "The stale command is no longer in the current command list.")
        XCTAssertEqual(replacement.prompt.lineID, cached.prompt.lineID, "A hard reset may reuse row IDs.")
        XCTAssertNotEqual(replacement.prompt.sourceSequence, cached.prompt.sourceSequence)
        XCTAssertNil(current.command(matching: cached))
        XCTAssertNotNil(current.command(matching: replacement))
    }
}
