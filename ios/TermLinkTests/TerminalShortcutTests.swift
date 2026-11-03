// SPDX-License-Identifier: GPL-3.0-only
import XCTest
@testable import TermLink

@MainActor
final class TerminalShortcutTests: XCTestCase {
    private func combination(label: String = "", key: UInt32 = 1, scalar: UInt32 = 0, shift: Bool = false, alt: Bool = false, control: Bool = false) -> TerminalCustomShortcut {
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
        // Printable character shortcuts are valid, matching Android's key parser.
        XCTAssertNotNil(combination(scalar: UInt32(Unicode.Scalar("x").value)).normalized)
        // Control characters never become combinations.
        XCTAssertNil(combination(scalar: 3, control: true).normalized)
        XCTAssertNil(combination(scalar: 0xD800, control: true).normalized)
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
        XCTAssertEqual(rows.top.map(\.label), ["0", "2", "4"])
        XCTAssertEqual(rows.bottom.map(\.label), ["1", "3"])
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

    func testMovePersistsOrderAndEditingKeepsPosition() throws {
        let name = "terminal-shortcuts-" + UUID().uuidString
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        let store = TerminalShortcutStore(defaults: defaults)
        for id in ["a", "b", "c", "d", "e"] {
            var value = combination(key: 16)
            value.id = id
            try store.upsert(value)
        }
        store.move(from: IndexSet([1, 3]), to: 5)
        XCTAssertEqual(store.customs.map(\.id), ["a", "c", "e", "b", "d"])
        var edited = store.customs[3]
        edited.label = "改名"
        try store.upsert(edited)
        XCTAssertEqual(store.customs.map(\.id), ["a", "c", "e", "b", "d"])
        store.twoRows = false
        let reloaded = TerminalShortcutStore(defaults: defaults)
        XCTAssertEqual(reloaded.customs, store.customs)
        XCTAssertFalse(reloaded.twoRows)
        store.move(from: IndexSet([9]), to: 0)
        store.move(from: IndexSet([0]), to: 8)
        XCTAssertEqual(store.customs, reloaded.customs)
    }

    func testDeleteAndRestoreMultipleShortcutsKeepsIdentityAndOrder() throws {
        let name = "terminal-shortcuts-" + UUID().uuidString
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        let store = TerminalShortcutStore(defaults: defaults)
        for id in ["a", "b", "c", "d"] {
            var value = combination(key: 16)
            value.id = id
            try store.upsert(value)
        }
        let original = store.customs
        let removal = try XCTUnwrap(store.remove(at: IndexSet([1, 3])))
        XCTAssertEqual(store.customs.map(\.id), ["a", "c"])
        try store.restore(removal)
        XCTAssertEqual(store.customs, original)
        try store.restore(removal)
        XCTAssertEqual(store.customs, original) // Repeated undo never duplicates an id.
        XCTAssertEqual(TerminalShortcutStore(defaults: defaults).customs, original)
    }

    func testLoadRepairsDuplicateInvalidAndExcessiveStoredEntries() throws {
        let name = "terminal-shortcuts-" + UUID().uuidString
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        var values: [TerminalCustomShortcut] = []
        for index in 0..<30 {
            var value = combination(key: 16)
            value.id = "item-\(index)"
            values.append(value)
        }
        var invalid = combination(key: 16)
        invalid.id = "invalid id"
        var encoded = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(values)) as? [Any])
        let invalidJSON = try JSONSerialization.jsonObject(with: JSONEncoder().encode(invalid))
        encoded.insert(invalidJSON, at: 0)
        encoded.insert(["id": "malformed"], at: 1)
        encoded.insert(NSNull(), at: 2)
        encoded.insert(encoded[3], at: 4) // Duplicate the first valid id.
        defaults.set(try JSONSerialization.data(withJSONObject: encoded), forKey: "terminal-shortcuts-custom")
        let store = TerminalShortcutStore(defaults: defaults)
        XCTAssertEqual(store.customs.map(\.id), (0..<24).map { "item-\($0)" })
        XCTAssertEqual(TerminalShortcutStore(defaults: defaults).customs, store.customs)
        defaults.set(Data("broken-json".utf8), forKey: "terminal-shortcuts-custom")
        XCTAssertTrue(TerminalShortcutStore(defaults: defaults).customs.isEmpty)
    }

    func testShiftOnlyShortcutSurvivesReloadAndEditing() throws {
        let name = "terminal-shortcuts-" + UUID().uuidString
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        let store = TerminalShortcutStore(defaults: defaults)
        let shortcut = combination(scalar: Unicode.Scalar("1").value, shift: true)
        let normalized = try XCTUnwrap(shortcut.normalized)
        XCTAssertEqual(normalized.normalized, normalized)
        try store.upsert(shortcut)
        let reloaded = TerminalShortcutStore(defaults: defaults)
        XCTAssertEqual(reloaded.customs, [normalized])
        var edited = try XCTUnwrap(reloaded.customs.first)
        edited.label = "感叹号"
        try reloaded.upsert(edited)
        XCTAssertEqual(reloaded.customs.first?.scalar, Unicode.Scalar("!").value)
        XCTAssertEqual(reloaded.customs.first?.id, shortcut.id)
        XCTAssertEqual(TerminalShortcutStore(defaults: defaults).customs, reloaded.customs)
    }
}
