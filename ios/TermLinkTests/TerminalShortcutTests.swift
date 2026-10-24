// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

@MainActor
final class TerminalShortcutTests: XCTestCase {
    private func combination(label: String = "", key: UInt32 = 1, scalar: UInt32, shift: Bool = false, alt: Bool = false, control: Bool = false) -> TerminalCustomShortcut {
        TerminalCustomShortcut(id: "test-1", label: label, kind: .combination, text: "", key: key, scalar: scalar, shift: shift, alt: alt, control: control)
    }

    func testNormalizationRejectsInvalidIDLabelAndQuickInput() {
        var value = combination(scalar: UInt32(Unicode.Scalar("x").value), control: true)
        value.id = "bad id!" // space and punctuation violate the id charset
        XCTAssertNil(value.normalized)
        value.id = String(repeating: "a", count: 65)
        XCTAssertNil(value.normalized)
        value.id = "ok-id"; value.label = String(repeating: "名", count: 20)
        XCTAssertEqual(value.normalized?.label.count, 16)
        var snippet = TerminalCustomShortcut(id: "s1", label: "", kind: .quickInput, text: "   ", key: 1, scalar: 0, shift: false, alt: false, control: false)
        XCTAssertNil(snippet.normalized)
        snippet.text = String(repeating: "字", count: 5000)
        XCTAssertEqual(snippet.normalized?.text.count, TerminalCustomShortcut.maxQuickInput)
    }

    func testCombinationValidation() {
        // A bare character without Ctrl/Alt adds nothing over the keyboard and is rejected.
        XCTAssertNil(combination(scalar: UInt32(Unicode.Scalar("x").value)).normalized)
        // Control characters never become combinations.
        XCTAssertNil(combination(scalar: 3, control: true).normalized)
        // Character with Ctrl is accepted; Shift-only characters fold into the scalar.
        let folded = combination(scalar: UInt32(Unicode.Scalar("1").value), shift: true).normalized
        XCTAssertEqual(folded?.scalar, UInt32(Unicode.Scalar("!").value))
        XCTAssertFalse(folded?.shift ?? true)
        let upper = combination(scalar: UInt32(Unicode.Scalar("a").value), shift: true).normalized
        XCTAssertEqual(upper?.scalar, UInt32(Unicode.Scalar("A").value))
        // Special keys accept any modifier combination, including none.
        XCTAssertNotNil(combination(key: 16, scalar: 0).normalized) // F1
        XCTAssertNotNil(combination(key: 10, scalar: 0, control: true).normalized) // Ctrl+PageUp
    }

    func testShiftedCodePointMirrorsAndroidMap() {
        XCTAssertEqual(TerminalCustomShortcut.shiftedCodePoint(UInt32(Unicode.Scalar("9").value)), UInt32(Unicode.Scalar("(").value))
        XCTAssertEqual(TerminalCustomShortcut.shiftedCodePoint(UInt32(Unicode.Scalar("-").value)), UInt32(Unicode.Scalar("_").value))
        XCTAssertEqual(TerminalCustomShortcut.shiftedCodePoint(UInt32(Unicode.Scalar("/").value)), UInt32(Unicode.Scalar("?").value))
        XCTAssertEqual(TerminalCustomShortcut.shiftedCodePoint(UInt32(Unicode.Scalar("z").value)), UInt32(Unicode.Scalar("Z").value))
        XCTAssertEqual(TerminalCustomShortcut.shiftedCodePoint(UInt32(Unicode.Scalar("中").value)), UInt32(Unicode.Scalar("中").value))
    }

    func testCanonicalLabelAndDisplayFallback() {
        let value = combination(label: "", key: 27, alt: true, control: true).normalized
        XCTAssertEqual(value?.canonical, "Ctrl+Alt+F12")
        XCTAssertEqual(value?.displayLabel, "Ctrl+Alt+F12")
        let named = combination(label: "重启服务", key: 1, scalar: UInt32(Unicode.Scalar("c").value), control: true).normalized
        XCTAssertEqual(named?.displayLabel, "重启服务")
    }

    func testBalancedRowsSplitCustoms() {
        let customs = (0..<5).map { combination(label: "\($0)", key: 16 + UInt32($0), scalar: 0).normalized! }
        let rows = TerminalShortcutLayout.balancedRows(customs)
        XCTAssertEqual(rows.top.count, 3); XCTAssertEqual(rows.bottom.count, 2)
        XCTAssertEqual(rows.top + rows.bottom, customs)
    }

    func testStoreUpsertRoundTripReplaceAndCap() throws {
        let defaults = UserDefaults(suiteName: "terminal-shortcut-tests")!
        defaults.removePersistentDomain(forName: "terminal-shortcut-tests")
        let store = TerminalShortcutStore(defaults: defaults)
        XCTAssertTrue(store.twoRows)
        var first = combination(label: "中断", key: 1, scalar: UInt32(Unicode.Scalar("c").value), control: true).normalized!
        first.id = "shortcut-a"
        try store.upsert(first)
        var second = combination(key: 16, scalar: 0).normalized!; second.id = "shortcut-b"
        try store.upsert(second)
        var replacement = combination(label: "改名", key: 16, scalar: 0).normalized!; replacement.id = "shortcut-a"
        try store.upsert(replacement)
        XCTAssertEqual(store.customs.map(\.id), ["shortcut-a", "shortcut-b"])
        XCTAssertEqual(store.customs.first?.label, "改名")
        // Persistence round-trips through the injected defaults.
        let reloaded = TerminalShortcutStore(defaults: defaults)
        XCTAssertEqual(reloaded.customs, store.customs)
        // The cap mirrors Android MAX_CUSTOM_TERMINAL_SHORTCUTS.
        for index in 0..<(TerminalCustomShortcut.maxShortcuts - 2) {
            var extra = combination(key: 16, scalar: 0).normalized!
            extra.id = "bulk-\(index)"
            try store.upsert(extra)
        }
        var overflow = combination(key: 16, scalar: 0).normalized!; overflow.id = "overflow"
        XCTAssertThrowsError(try store.upsert(overflow))
        XCTAssertThrowsError(try store.upsert(combination(scalar: 0))) // invalid: character key without scalar
    }
}
